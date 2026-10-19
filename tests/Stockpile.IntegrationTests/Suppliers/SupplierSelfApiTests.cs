using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Stockpile.Contracts.Errors;
using Stockpile.IntegrationTests.Testing;

namespace Stockpile.IntegrationTests.Suppliers;

/// <summary>
/// business-flows §3.3 stages 2–5 — the signed-in supplier's own onboarding
/// surface over real HTTP (api.md §2.9 <c>/suppliers/me</c>): the resumable
/// person/business profile, the district service areas and the category
/// coverage.
/// <para>
/// What this pins: every endpoint is authenticated and scoped to the caller's
/// own supplier (BOLA, api.md §1.6) — a buyer has no supplier and answers one
/// 404, and the owner-only writes answer 403 without the capability; the
/// profile <c>PATCH</c> merges per member (a null member is untouched, a blank
/// one clears) so a stage is resumable; service areas REPLACE rather than
/// append; and the catalogue and the reference geography remain the authority
/// on which slugs and districts exist.
/// </para>
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class SupplierSelfApiTests
{
    private const string ApiBase = "/api/v1";
    private const string SupplierBase = $"{ApiBase}/suppliers/me";
    private const string Password = "correct-horse-battery-staple";

    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    private static readonly string[] HardwareCategory = ["hardware"];
    private static readonly string[] ColomboAndGampaha = ["colombo", "gampaha"];
    private static readonly string[] KandyOnly = ["kandy"];
    private static readonly string[] UnknownDistrict = ["atlantis"];

    private readonly StockpileApiFactory _factory;

    public SupplierSelfApiTests(StockpileApiFactory factory) => _factory = factory;

    private static string NewEmail(string prefix) => $"{prefix}.{Guid.NewGuid():N}@test.local";

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string route, object? payload, string requestToken)
    {
        using var request = new HttpRequestMessage(method, route);
        if (payload is not null)
        {
            request.Content = JsonContent.Create(payload, options: Wire);
        }

        return await AntiforgeryTestSupport.SendUnsafeAsync(client, request, requestToken);
    }

    /// <summary>Registers a fresh supplier and signs it in, returning the client with the antiforgery token.</summary>
    private async Task<(HttpClient Client, string Token)> SignInAsSupplierAsync()
    {
        HttpClient client = AntiforgeryTestSupport.HttpsClient(_factory);
        string email = NewEmail("supplier");
        string token = await AntiforgeryTestSupport.AcquireTokenAsync(client);

        using (HttpResponseMessage response = await SendAsync(
            client, HttpMethod.Post, $"{ApiBase}/auth/register/supplier", new
            {
                email,
                password = Password,
                contactFirstName = "Nimal",
                contactLastName = "Perera",
                phone = "+94771234567",
                companyName = "Perera Hardware (Pvt) Ltd",
                registrationNumber = "PV-12345",
                website = "https://pererahardware.example",
                categories = HardwareCategory,
            }, token))
        {
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        token = await AntiforgeryTestSupport.AcquireTokenAsync(client);
        using (HttpResponseMessage login = await SendAsync(
            client, HttpMethod.Post, $"{ApiBase}/auth/login", new { email, password = Password }, token))
        {
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        }

        return (client, await AntiforgeryTestSupport.AcquireTokenAsync(client));
    }

    private static async Task<SelfPayload> GetSelfAsync(HttpClient client)
    {
        using HttpResponseMessage response = await client.GetAsync(SupplierBase);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<SelfPayload>(Wire)
            ?? throw new InvalidOperationException("GET /suppliers/me answered without a body.");
    }

    // ---- Authentication and scoping floor -------------------------------------

    [Fact]
    public async Task A_caller_without_a_supplier_answers_404_and_a_write_needs_the_owner_capability()
    {
        // Anonymous first: the summary read sits behind authentication, and the
        // literal /suppliers/me route must win over the public /suppliers/{slug}
        // directory route (a 200 here would mean the directory answered for a
        // supplier literally named "me").
        HttpClient anonymous = AntiforgeryTestSupport.HttpsClient(_factory);
        using (HttpResponseMessage anon = await anonymous.GetAsync(SupplierBase))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, anon.StatusCode);
            ErrorEnvelope envelope = await anon.Content.ReadFromJsonAsync<ErrorEnvelope>()
                ?? throw new InvalidOperationException("anonymous GET answered without an envelope.");
            Assert.Equal(ErrorCodes.Unauthenticated, envelope.Code);
        }

        // A signed-in buyer has no supplier: the read is 404, and the owner-only
        // write is refused on capability before any supplier lookup.
        HttpClient buyer = AntiforgeryTestSupport.HttpsClient(_factory);
        string email = NewEmail("buyer");
        string token = await AntiforgeryTestSupport.AcquireTokenAsync(buyer);
        using (HttpResponseMessage register = await SendAsync(buyer, HttpMethod.Post, $"{ApiBase}/auth/register", new
        {
            firstName = "Test",
            lastName = "Buyer",
            email,
            phone = "+94771234567",
            password = Password,
        }, token))
        {
            Assert.Equal(HttpStatusCode.Created, register.StatusCode);
        }

        token = await AntiforgeryTestSupport.AcquireTokenAsync(buyer);
        using (HttpResponseMessage login = await SendAsync(
            buyer, HttpMethod.Post, $"{ApiBase}/auth/login", new { email, password = Password }, token))
        {
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        }

        using (HttpResponseMessage read = await buyer.GetAsync(SupplierBase))
        {
            Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
            ErrorEnvelope envelope = await read.Content.ReadFromJsonAsync<ErrorEnvelope>()
                ?? throw new InvalidOperationException("404 answered without an envelope.");
            Assert.Equal(ErrorCodes.ResourceNotFound, envelope.Code);
        }

        string buyerToken = await AntiforgeryTestSupport.AcquireTokenAsync(buyer);
        using (HttpResponseMessage write = await SendAsync(
            buyer, HttpMethod.Patch, $"{SupplierBase}/profile", new { phone = "+94770000000" }, buyerToken))
        {
            Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
            ErrorEnvelope envelope = await write.Content.ReadFromJsonAsync<ErrorEnvelope>()
                ?? throw new InvalidOperationException("403 answered without an envelope.");
            Assert.Equal(ErrorCodes.InsufficientCapability, envelope.Code);
        }
    }

    [Fact]
    public async Task The_self_surface_only_ever_reports_the_callers_own_supplier()
    {
        (HttpClient first, _) = await SignInAsSupplierAsync();
        (HttpClient second, string secondToken) = await SignInAsSupplierAsync();

        SelfPayload firstSelf = await GetSelfAsync(first);
        SelfPayload secondSelf = await GetSelfAsync(second);
        Assert.NotEqual(firstSelf.SupplierId, secondSelf.SupplierId);
        Assert.NotEqual(firstSelf.Slug, secondSelf.Slug);

        // The second supplier's write is its own; the first supplier's rows are
        // never returned to it.
        using (HttpResponseMessage write = await SendAsync(
            second, HttpMethod.Patch, $"{SupplierBase}/profile", new { contactPerson = "Second Supplier Contact" }, secondToken))
        {
            Assert.Equal(HttpStatusCode.OK, write.StatusCode);
        }

        SelfPayload firstAgain = await GetSelfAsync(first);
        Assert.Equal(firstSelf.SupplierId, firstAgain.SupplierId);
        Assert.NotEqual("Second Supplier Contact", firstAgain.Profile.ContactPerson);
    }

    // ---- Stage 2/3 — the person and business profile ---------------------------

    [Fact]
    public async Task The_profile_merge_is_partial_clears_on_blank_and_survives_a_re_read()
    {
        (HttpClient client, string token) = await SignInAsSupplierAsync();

        // Registration already published the contact person composed from the
        // account's first/last name; the merge must not invent anything else.
        SelfPayload registered = await GetSelfAsync(client);
        Assert.StartsWith("perera-hardware-pvt-ltd", registered.Slug, StringComparison.Ordinal);
        Assert.Equal("Active", registered.Status);
        Assert.Equal("Unverified", registered.VerificationState);
        Assert.Equal("Perera Hardware (Pvt) Ltd", registered.CompanyName);
        Assert.Contains(registered.Categories, category => category.Slug == "hardware");
        Assert.Empty(registered.ServiceAreas);

        using (HttpResponseMessage merge = await SendAsync(client, HttpMethod.Patch, $"{SupplierBase}/profile", new
        {
            contactPerson = "Nimal Perera",
            phone = "+94771111111",
            website = "https://perera.example",
        }, token))
        {
            Assert.Equal(HttpStatusCode.OK, merge.StatusCode);
            SelfPayload self = await merge.Content.ReadFromJsonAsync<SelfPayload>(Wire)
                ?? throw new InvalidOperationException("PATCH answered without a body.");
            Assert.Equal("Nimal Perera", self.Profile.ContactPerson);
            Assert.Equal("+94771111111", self.Profile.Phone);
            Assert.Equal("https://perera.example", self.Profile.Website);
        }

        // A call naming only the company leaves every other member untouched.
        string token2 = await AntiforgeryTestSupport.AcquireTokenAsync(client);
        using (HttpResponseMessage restyle = await SendAsync(client, HttpMethod.Patch, $"{SupplierBase}/profile",
            new { companyName = "Perera Hardware & Tools (Pvt) Ltd" }, token2))
        {
            Assert.Equal(HttpStatusCode.OK, restyle.StatusCode);
            SelfPayload self = await restyle.Content.ReadFromJsonAsync<SelfPayload>(Wire)
                ?? throw new InvalidOperationException("PATCH answered without a body.");
            Assert.Equal("Perera Hardware & Tools (Pvt) Ltd", self.DisplayName);
            Assert.Equal("Perera Hardware & Tools (Pvt) Ltd", self.CompanyName);
            Assert.Equal("Nimal Perera", self.Profile.ContactPerson);
            Assert.Equal("+94771111111", self.Profile.Phone);
        }

        // A blank member clears the column; an untouched one still survives.
        string token3 = await AntiforgeryTestSupport.AcquireTokenAsync(client);
        using (HttpResponseMessage clear = await SendAsync(client, HttpMethod.Patch, $"{SupplierBase}/profile",
            new { phone = string.Empty }, token3))
        {
            Assert.Equal(HttpStatusCode.OK, clear.StatusCode);
            SelfPayload self = await clear.Content.ReadFromJsonAsync<SelfPayload>(Wire)
                ?? throw new InvalidOperationException("PATCH answered without a body.");
            Assert.Null(self.Profile.Phone);
            Assert.Equal("Nimal Perera", self.Profile.ContactPerson);
        }

        // The whole merge survives a fresh read, not just the write's own echo.
        SelfPayload reRead = await GetSelfAsync(client);
        Assert.Null(reRead.Profile.Phone);
        Assert.Equal("Nimal Perera", reRead.Profile.ContactPerson);
        Assert.Equal("Perera Hardware & Tools (Pvt) Ltd", reRead.DisplayName);
    }

    // ---- Stage 5 — category coverage -------------------------------------------

    [Fact]
    public async Task Category_coverage_adds_idempotently_and_removes_by_slug()
    {
        (HttpClient client, string token) = await SignInAsSupplierAsync();

        using (HttpResponseMessage add = await SendAsync(
            client, HttpMethod.Post, $"{SupplierBase}/categories", new { slug = "plumbing" }, token))
        {
            Assert.Equal(HttpStatusCode.OK, add.StatusCode);
            SelfPayload self = await add.Content.ReadFromJsonAsync<SelfPayload>(Wire)
                ?? throw new InvalidOperationException("POST categories answered without a body.");
            Assert.Contains(self.Categories, category => category.Slug == "plumbing");
            Assert.Contains(self.Categories, category => category.Slug == "hardware");
        }

        // Re-adding is a no-op, not a duplicate row.
        string token2 = await AntiforgeryTestSupport.AcquireTokenAsync(client);
        using (HttpResponseMessage again = await SendAsync(
            client, HttpMethod.Post, $"{SupplierBase}/categories", new { slug = "plumbing" }, token2))
        {
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
            SelfPayload self = await again.Content.ReadFromJsonAsync<SelfPayload>(Wire)
                ?? throw new InvalidOperationException("POST categories answered without a body.");
            Assert.Single(self.Categories, category => category.Slug == "plumbing");
        }

        // The catalogue is the authority: an unknown slug is 422 naming the field.
        string token3 = await AntiforgeryTestSupport.AcquireTokenAsync(client);
        using (HttpResponseMessage unknown = await SendAsync(
            client, HttpMethod.Post, $"{SupplierBase}/categories", new { slug = "not-a-real-department" }, token3))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.StatusCode);
            ErrorEnvelope envelope = await unknown.Content.ReadFromJsonAsync<ErrorEnvelope>()
                ?? throw new InvalidOperationException("422 answered without an envelope.");
            Assert.Equal(ErrorCodes.ValidationFailed, envelope.Code);
            Assert.Equal("slug", Assert.Single(envelope.FieldErrors!).Field);
        }

        string token4 = await AntiforgeryTestSupport.AcquireTokenAsync(client);
        using (HttpResponseMessage remove = await SendAsync(
            client, HttpMethod.Delete, $"{SupplierBase}/categories/plumbing", null, token4))
        {
            Assert.Equal(HttpStatusCode.OK, remove.StatusCode);
            SelfPayload self = await remove.Content.ReadFromJsonAsync<SelfPayload>(Wire)
                ?? throw new InvalidOperationException("DELETE categories answered without a body.");
            Assert.DoesNotContain(self.Categories, category => category.Slug == "plumbing");
            Assert.Contains(self.Categories, category => category.Slug == "hardware");
        }

        string token5 = await AntiforgeryTestSupport.AcquireTokenAsync(client);
        using (HttpResponseMessage gone = await SendAsync(
            client, HttpMethod.Delete, $"{SupplierBase}/categories/plumbing", null, token5))
        {
            Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        }
    }

    // ---- Stage 4 — district service areas ---------------------------------------

    [Fact]
    public async Task Service_areas_replace_the_whole_set_and_validate_against_reference_geography()
    {
        (HttpClient client, string token) = await SignInAsSupplierAsync();

        using (HttpResponseMessage set = await SendAsync(client, HttpMethod.Put, $"{SupplierBase}/service-areas",
            new { districts = ColomboAndGampaha, defaultDistrict = "colombo" }, token))
        {
            Assert.Equal(HttpStatusCode.OK, set.StatusCode);
            SelfPayload self = await set.Content.ReadFromJsonAsync<SelfPayload>(Wire)
                ?? throw new InvalidOperationException("PUT service-areas answered without a body.");
            Assert.Equal(2, self.ServiceAreas.Count);
            Assert.Equal("colombo", Assert.Single(self.ServiceAreas, area => area.IsDefault).DistrictCode);
        }

        // Replace, do not append.
        string token2 = await AntiforgeryTestSupport.AcquireTokenAsync(client);
        using (HttpResponseMessage replace = await SendAsync(client, HttpMethod.Put, $"{SupplierBase}/service-areas",
            new { districts = KandyOnly }, token2))
        {
            Assert.Equal(HttpStatusCode.OK, replace.StatusCode);
            SelfPayload self = await replace.Content.ReadFromJsonAsync<SelfPayload>(Wire)
                ?? throw new InvalidOperationException("PUT service-areas answered without a body.");
            Assert.Equal("kandy", Assert.Single(self.ServiceAreas).DistrictCode);
            Assert.False(self.ServiceAreas[0].IsDefault);
        }

        // An unknown district is refused (the reference table is the authority).
        string token3 = await AntiforgeryTestSupport.AcquireTokenAsync(client);
        using (HttpResponseMessage unknown = await SendAsync(client, HttpMethod.Put, $"{SupplierBase}/service-areas",
            new { districts = UnknownDistrict }, token3))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.StatusCode);
            ErrorEnvelope envelope = await unknown.Content.ReadFromJsonAsync<ErrorEnvelope>()
                ?? throw new InvalidOperationException("422 answered without an envelope.");
            Assert.Equal(ErrorCodes.ValidationFailed, envelope.Code);
            Assert.Equal("districts", Assert.Single(envelope.FieldErrors!).Field);
        }

        // A default that is not one of the chosen districts is refused too.
        string token4 = await AntiforgeryTestSupport.AcquireTokenAsync(client);
        using (HttpResponseMessage badDefault = await SendAsync(client, HttpMethod.Put, $"{SupplierBase}/service-areas",
            new { districts = KandyOnly, defaultDistrict = "colombo" }, token4))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, badDefault.StatusCode);
            ErrorEnvelope envelope = await badDefault.Content.ReadFromJsonAsync<ErrorEnvelope>()
                ?? throw new InvalidOperationException("422 answered without an envelope.");
            Assert.Equal("defaultDistrict", Assert.Single(envelope.FieldErrors!).Field);
        }
    }

    // ---- Wire shapes -------------------------------------------------------------

    private sealed record SelfPayload(
        Guid SupplierId,
        string Slug,
        string DisplayName,
        string Status,
        string VerificationState,
        string CompanyName,
        string? RegistrationNumber,
        string? Website,
        ProfilePayload Profile,
        List<CategoryPayload> Categories,
        List<ServiceAreaPayload> ServiceAreas);

    private sealed record ProfilePayload(string? ContactPerson, string? Phone, string? Email, string? Website);

    private sealed record CategoryPayload(string Slug, string Name, string SpecialtyLevel, bool Quotable);

    private sealed record ServiceAreaPayload(string DistrictCode, bool IsDefault, int? RadiusKm);
}
