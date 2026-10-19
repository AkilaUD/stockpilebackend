using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Stockpile.Contracts.Authentication;
using Stockpile.Contracts.Errors;
using Stockpile.Domain.Entities.Identity;
using Stockpile.Infrastructure.Persistence;
using Stockpile.IntegrationTests.Testing;

namespace Stockpile.IntegrationTests.Auth;

/// <summary>
/// business-flows §2/§3 — the resumable registration draft over real HTTP
/// (api.md §2.1 <c>POST /auth/registration-drafts</c> and <c>/resume</c>). A
/// visitor with no account autosaves the non-secret part of their registration,
/// gets back an opaque <c>draftToken</c>, and picks the form up later from that
/// token alone.
/// <para>
/// The invariants this pins: the token is minted once and addresses ONE draft;
/// the database stores only its SHA-256 hash (never the token, never a
/// password); a draft is retired the moment the registration it described
/// completes; and every dead token — unknown, expired, retired, or belonging to
/// the other flow — answers one identical 404 that reveals nothing.
/// </para>
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class RegistrationDraftApiTests
{
    private const string ApiBase = "/api/v1";
    private const string DraftRoute = $"{ApiBase}/auth/registration-drafts";
    private const string Password = "correct-horse-battery-staple";

    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    private static readonly string[] SupplierCategories = ["hardware", "roofing"];
    private static readonly string[] RoofingOnly = ["roofing"];

    private readonly StockpileApiFactory _factory;

    public RegistrationDraftApiTests(StockpileApiFactory factory) => _factory = factory;

    private static string NewEmail() => $"draft.{Guid.NewGuid():N}@test.local";

    private static SaveDraftPayload BuyerDraft(string? draftToken = null, string? email = null) => new(
        Kind: "buyer",
        DraftToken: draftToken,
        Email: email ?? NewEmail(),
        FirstName: "Kamal",
        LastName: "Silva",
        Phone: "+94771112233",
        CompanyName: null,
        RegistrationNumber: null,
        Website: null,
        Categories: null);

    private static SaveDraftPayload SupplierDraft(string? draftToken = null, string? email = null) => new(
        Kind: "supplier",
        DraftToken: draftToken,
        Email: email ?? NewEmail(),
        FirstName: "Nimal",
        LastName: "Perera",
        Phone: "+94771234567",
        CompanyName: "Perera Hardware (Pvt) Ltd",
        RegistrationNumber: "PV-12345",
        Website: "https://pererahardware.example",
        Categories: ["hardware", "roofing"]);

    private static async Task<HttpResponseMessage> PostUnsafeAsync(
        HttpClient client, string route, object payload, string requestToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, route)
        {
            Content = JsonContent.Create(payload, options: Wire),
        };
        return await AntiforgeryTestSupport.SendUnsafeAsync(client, request, requestToken);
    }

    private static async Task<DraftStatePayload> SaveAsync(
        HttpClient client, SaveDraftPayload payload)
    {
        string requestToken = await AntiforgeryTestSupport.AcquireTokenAsync(client);
        using var response = await PostUnsafeAsync(client, DraftRoute, payload, requestToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var state = await response.Content.ReadFromJsonAsync<DraftStatePayload>();
        return state!;
    }

    private static async Task<HttpResponseMessage> ResumeAsync(HttpClient client, string draftToken)
    {
        string requestToken = await AntiforgeryTestSupport.AcquireTokenAsync(client);
        return await PostUnsafeAsync(client, $"{DraftRoute}/resume", new { draftToken }, requestToken);
    }

    [Fact]
    public async Task Save_mints_a_token_and_resume_returns_the_non_secret_prefill()
    {
        using var client = AntiforgeryTestSupport.HttpsClient(_factory);
        SaveDraftPayload payload = SupplierDraft();

        DraftStatePayload created = await SaveAsync(client, payload);

        Assert.False(string.IsNullOrWhiteSpace(created.DraftToken));
        Assert.Equal("supplier", created.Kind);
        Assert.True(created.ExpiresAtUtc > DateTime.UtcNow);
        Assert.Equal(payload.Email, created.Prefill!.Email);
        Assert.Equal(SupplierCategories, created.Prefill.Categories);

        // The draft survives the browser: the token alone brings the prefill back.
        using var resumed = await ResumeAsync(client, created.DraftToken!);
        Assert.Equal(HttpStatusCode.OK, resumed.StatusCode);
        var state = await resumed.Content.ReadFromJsonAsync<DraftStatePayload>();
        Assert.Equal(payload.Email, state!.Prefill!.Email);
        Assert.Equal(payload.CompanyName, state.Prefill.CompanyName);
        Assert.Equal(payload.Phone, state.Prefill.Phone);
        Assert.DoesNotContain("password", await resumed.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_database_stores_only_the_hash_of_the_token()
    {
        using var client = AntiforgeryTestSupport.HttpsClient(_factory);
        SaveDraftPayload payload = BuyerDraft();
        DraftStatePayload created = await SaveAsync(client, payload);

        using IServiceScope scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StockpileDbContext>();
        RegistrationDraft row = await db.RegistrationDrafts
            .AsNoTracking()
            .SingleAsync(draft => draft.Email == payload.Email);

        Assert.Equal(64, row.TokenHash.Length); // SHA-256 hex
        Assert.DoesNotContain(created.DraftToken!, row.TokenHash, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("buyer", row.Kind);
        Assert.Null(row.CompletedAtUtc);
        Assert.Null(row.UserId);
    }

    [Fact]
    public void A_draft_neither_stores_nor_transports_a_password()
    {
        // The mechanic's whole safety story: a draft holds prefill, never a
        // credential. Guard it structurally so a future field cannot slip in.
        static bool HasPasswordMember(Type type) => type.GetProperties()
            .Any(property => property.Name.Contains("password", StringComparison.OrdinalIgnoreCase));

        Assert.False(HasPasswordMember(typeof(RegistrationDraft)));
        Assert.False(HasPasswordMember(typeof(RegistrationDraftPrefill)));
        Assert.False(HasPasswordMember(typeof(SaveDraftPayload)));
    }

    [Fact]
    public async Task Saving_again_with_a_live_token_updates_the_same_draft()
    {
        using var client = AntiforgeryTestSupport.HttpsClient(_factory);
        SaveDraftPayload first = SupplierDraft();
        DraftStatePayload created = await SaveAsync(client, first);

        DraftStatePayload updated = await SaveAsync(
            client,
            first with
            {
                DraftToken = created.DraftToken,
                CompanyName = "Perera Hardware & Roofing (Pvt) Ltd",
                Categories = ["roofing"],
            });

        Assert.Equal(created.DraftToken, updated.DraftToken); // same draft, same token
        Assert.Equal("Perera Hardware & Roofing (Pvt) Ltd", updated.Prefill!.CompanyName);
        Assert.Equal(RoofingOnly, updated.Prefill.Categories);

        using IServiceScope scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StockpileDbContext>();
        RegistrationDraft row = await db.RegistrationDrafts
            .Include(draft => draft.Categories)
            .AsNoTracking()
            .SingleAsync(draft => draft.Email == first.Email);
        Assert.Equal("Perera Hardware & Roofing (Pvt) Ltd", row.CompanyName);
        Assert.Equal(RoofingOnly, row.Categories.Select(category => category.Slug));
    }

    [Fact]
    public async Task Resume_with_an_unknown_token_is_404()
    {
        using var client = AntiforgeryTestSupport.HttpsClient(_factory);

        using var response = await ResumeAsync(client, "not-a-real-draft-token");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ErrorEnvelope>();
        Assert.Equal(ErrorCodes.ResourceNotFound, envelope!.Code);
    }

    [Fact]
    public async Task A_token_from_the_other_flow_cannot_address_a_draft()
    {
        using var client = AntiforgeryTestSupport.HttpsClient(_factory);
        DraftStatePayload buyer = await SaveAsync(client, BuyerDraft());

        // Presenting a buyer draft to the supplier flow answers exactly like a
        // dead token — the reason never leaks.
        string requestToken = await AntiforgeryTestSupport.AcquireTokenAsync(client);
        using var response = await PostUnsafeAsync(
            client, DraftRoute, SupplierDraft(draftToken: buyer.DraftToken), requestToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ErrorEnvelope>();
        Assert.Equal(ErrorCodes.ResourceNotFound, envelope!.Code);
    }

    [Fact]
    public async Task A_draft_with_an_unknown_kind_is_422()
    {
        using var client = AntiforgeryTestSupport.HttpsClient(_factory);
        string requestToken = await AntiforgeryTestSupport.AcquireTokenAsync(client);

        using var response = await PostUnsafeAsync(
            client, DraftRoute, BuyerDraft() with { Kind = "vendor" }, requestToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ErrorEnvelope>();
        Assert.Equal(ErrorCodes.ValidationFailed, envelope!.Code);
        Assert.Equal("kind", Assert.Single(envelope.FieldErrors!).Field);
    }

    [Fact]
    public async Task Completing_the_registration_retires_the_draft_and_it_can_never_resume()
    {
        using var client = AntiforgeryTestSupport.HttpsClient(_factory);
        SaveDraftPayload payload = BuyerDraft();
        DraftStatePayload created = await SaveAsync(client, payload);

        string requestToken = await AntiforgeryTestSupport.AcquireTokenAsync(client);
        using (var registration = await PostUnsafeAsync(
            client,
            $"{ApiBase}/auth/register",
            new { email = payload.Email, password = Password, draftToken = created.DraftToken },
            requestToken))
        {
            Assert.Equal(HttpStatusCode.Created, registration.StatusCode);
        }

        using IServiceScope scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StockpileDbContext>();
        RegistrationDraft row = await db.RegistrationDrafts
            .AsNoTracking()
            .SingleAsync(draft => draft.Email == payload.Email);
        Assert.NotNull(row.CompletedAtUtc);
        Assert.NotNull(row.UserId);
        Assert.False(string.IsNullOrWhiteSpace(row.TokenHash)); // still only ever the hash

        // Single-use: the retired draft's token is indistinguishable from a dead one.
        using var resumed = await ResumeAsync(client, created.DraftToken!);
        Assert.Equal(HttpStatusCode.NotFound, resumed.StatusCode);
    }

    [Fact]
    public async Task An_expired_draft_is_404_and_is_swept_when_the_next_draft_is_minted()
    {
        using var client = AntiforgeryTestSupport.HttpsClient(_factory);
        SaveDraftPayload payload = BuyerDraft();
        DraftStatePayload created = await SaveAsync(client, payload);

        // Age the row out of its window (the TTL itself is validated at boot;
        // this drives the expiry decision without waiting a real interval).
        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StockpileDbContext>();
            RegistrationDraft row = await db.RegistrationDrafts
                .SingleAsync(draft => draft.Email == payload.Email);
            row.ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }

        using (var expired = await ResumeAsync(client, created.DraftToken!))
        {
            Assert.Equal(HttpStatusCode.NotFound, expired.StatusCode);
        }

        // Minting the next draft sweeps what has expired, so an anonymous flow
        // cannot grow rows forever.
        _ = await SaveAsync(client, BuyerDraft());

        using IServiceScope verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<StockpileDbContext>();
        Assert.False(await verifyDb.RegistrationDrafts.AnyAsync(draft => draft.Email == payload.Email));
    }

    // ---- Wire shapes -----------------------------------------------------------

    private sealed record SaveDraftPayload(
        string Kind,
        string? DraftToken,
        string? Email,
        string? FirstName,
        string? LastName,
        string? Phone,
        string? CompanyName,
        string? RegistrationNumber,
        string? Website,
        IReadOnlyList<string>? Categories);

    private sealed record DraftStatePayload(
        string? DraftToken,
        string? Kind,
        DateTime ExpiresAtUtc,
        PrefillPayload? Prefill);

    private sealed record PrefillPayload(
        string? Email,
        string? FirstName,
        string? LastName,
        string? Phone,
        string? CompanyName,
        string? RegistrationNumber,
        string? Website,
        IReadOnlyList<string>? Categories);
}
