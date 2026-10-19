using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Stockpile.Contracts.Authentication;
using Stockpile.Contracts.Errors;
using Stockpile.Domain.Entities.Catalogue;
using Stockpile.Domain.Entities.Identity;
using Stockpile.Domain.Enums;
using Stockpile.Infrastructure.Persistence;
using Stockpile.IntegrationTests.Testing;

namespace Stockpile.IntegrationTests.Auth;

/// <summary>
/// Task 2F — supplier self-registration over real HTTP (business-flows §3.3,
/// api.md §2.1 <c>POST /auth/register/supplier</c>): one anonymous, rate-limited
/// submit creates the account holding the supplier role plus — in one
/// transaction — the supplier company, the caller's accepted membership, the
/// supplier aggregate with its public profile and its category coverage. The
/// taxonomy is the authority on which slugs exist, so a submit naming no real
/// department is refused before any account is created.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class SupplierRegistrationApiTests
{
    private const string CompanyName = "Perera Hardware (Pvt) Ltd";
    private const string Password = "correct-horse-battery-staple";
    private const string ApiBase = "/api/v1";

    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    private readonly StockpileApiFactory _factory;

    /// <summary>Shares the one API host (single migrate + seed of Stockpile_Test).</summary>
    public SupplierRegistrationApiTests(StockpileApiFactory factory) => _factory = factory;

    private static string NewEmail() => $"supplier.{Guid.NewGuid():N}@test.local";

    private static RegisterSupplierPayload Payload(string email, params string[] categories) => new(
        Email: email,
        Password: Password,
        ContactFirstName: "Nimal",
        ContactLastName: "Perera",
        Phone: "+94771234567",
        CompanyName: CompanyName,
        RegistrationNumber: "PV-12345",
        Website: "https://pererahardware.example",
        Categories: categories.Length == 0 ? ["hardware"] : categories);

    private static async Task<HttpResponseMessage> PostAsJsonAsync(
        HttpClient client, string route, object payload, string requestToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, route)
        {
            Content = JsonContent.Create(payload, options: Wire),
        };
        return await AntiforgeryTestSupport.SendUnsafeAsync(client, request, requestToken);
    }

    [Fact]
    public async Task Register_supplier_creates_the_account_company_membership_and_coverage()
    {
        using var client = AntiforgeryTestSupport.HttpsClient(_factory);
        string token = await AntiforgeryTestSupport.AcquireTokenAsync(client);
        string email = NewEmail();

        using var response = await PostAsJsonAsync(client, $"{ApiBase}/auth/register/supplier", Payload(email), token);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("/api/v1/auth/session", response.Headers.Location?.ToString());
        var body = await response.Content.ReadFromJsonAsync<RegisterResponsePayload>();
        Assert.Equal("registered", body!.Status);

        // The account signs in holding the supplier role and its capabilities.
        using var login = await PostAsJsonAsync(
            client, $"{ApiBase}/auth/login", new { email, password = Password }, body.RequestToken!);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var loginBody = await login.Content.ReadFromJsonAsync<LoginPayload>();
        Assert.Contains("supplierOwner", loginBody!.User!.Roles);
        Assert.Contains("product.create", loginBody.User.Permissions);
        Guid userPublicId = Guid.Parse(loginBody.User.PublicId);

        // …and one commit produced the whole supplier: company, membership,
        // aggregate (Unverified, catalogue-visible) and its department coverage.
        // The shared Stockpile_Test database persists between runs, so the
        // lookup follows this run's own membership rather than the company name.
        using IServiceScope scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StockpileDbContext>();
        Guid organizationId = await db.OrganizationMembers
            .Where(membership => membership.UserId == userPublicId && membership.RoleCode == "supplierOwner")
            .Select(membership => membership.OrganizationId)
            .SingleAsync();
        Organization organization = await db.Organizations.SingleAsync(o => o.PublicId == organizationId);
        Assert.Equal(OrganizationType.SupplierCompany, organization.Type);
        Assert.True(await db.OrganizationMembers.AnyAsync(membership =>
            membership.OrganizationId == organizationId
            && membership.UserId == userPublicId
            && membership.InvitationStatus == InvitationStatus.Accepted));

        Supplier supplier = await db.Suppliers.SingleAsync(s => s.OrganizationId == organizationId);
        Assert.StartsWith("perera-hardware-pvt-ltd", supplier.Slug, StringComparison.Ordinal);
        Assert.Equal(VerificationState.Unverified, supplier.VerificationState);
        Assert.Equal(SupplierStatus.Active, supplier.Status);
        Assert.True(await db.SupplierCategories.AnyAsync(coverage => coverage.SupplierId == supplier.PublicId));
    }

    [Fact]
    public async Task Register_supplier_with_no_known_category_is_422_before_any_account_exists()
    {
        using var client = AntiforgeryTestSupport.HttpsClient(_factory);
        string token = await AntiforgeryTestSupport.AcquireTokenAsync(client);

        using var response = await PostAsJsonAsync(
            client, $"{ApiBase}/auth/register/supplier", Payload(NewEmail(), "not-a-real-department"), token);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ErrorEnvelope>();
        Assert.Equal(ErrorCodes.ValidationFailed, envelope!.Code);
        Assert.Equal("categories", Assert.Single(envelope.FieldErrors!).Field);
    }

    [Fact]
    public async Task Register_supplier_with_a_duplicate_email_is_422_with_an_email_field_error()
    {
        using var client = AntiforgeryTestSupport.HttpsClient(_factory);
        string token = await AntiforgeryTestSupport.AcquireTokenAsync(client);
        RegisterSupplierPayload payload = Payload(NewEmail());

        using (var first = await PostAsJsonAsync(client, $"{ApiBase}/auth/register/supplier", payload, token))
        {
            Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        }

        // Re-acquire: the first register re-minted the double-submit cookie.
        string freshToken = await AntiforgeryTestSupport.AcquireTokenAsync(client);
        using var duplicate = await PostAsJsonAsync(client, $"{ApiBase}/auth/register/supplier", payload, freshToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, duplicate.StatusCode);
        var envelope = await duplicate.Content.ReadFromJsonAsync<ErrorEnvelope>();
        Assert.Equal(ErrorCodes.ValidationFailed, envelope!.Code);
        FieldError fieldError = Assert.Single(envelope.FieldErrors!);
        Assert.Equal("email", fieldError.Field);
        Assert.Equal("Duplicate", fieldError.Code);
    }

    [Fact]
    public async Task Register_supplier_with_missing_fields_is_422_with_field_errors()
    {
        using var client = AntiforgeryTestSupport.HttpsClient(_factory);
        string token = await AntiforgeryTestSupport.AcquireTokenAsync(client);

        var incomplete = Payload(NewEmail()) with { CompanyName = string.Empty, Categories = [] };
        using var response = await PostAsJsonAsync(client, $"{ApiBase}/auth/register/supplier", incomplete, token);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ErrorEnvelope>();
        Assert.Equal(ErrorCodes.ValidationFailed, envelope!.Code);
        Assert.NotEmpty(envelope.FieldErrors!); // validator rejects blank company + empty categories
    }

    // ---- Wire shapes -----------------------------------------------------------

    private sealed record RegisterSupplierPayload(
        string Email,
        string Password,
        string ContactFirstName,
        string ContactLastName,
        string Phone,
        string CompanyName,
        string? RegistrationNumber,
        string? Website,
        IReadOnlyList<string> Categories);

    private sealed record RegisterResponsePayload(string? Status, string? RequestToken);

    private sealed record LoginPayload(string? RequestToken, AuthSessionResponse? User);
}
