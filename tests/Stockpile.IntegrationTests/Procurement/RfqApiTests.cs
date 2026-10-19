using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Stockpile.Contracts.Errors;
using Stockpile.Contracts.Procurement;
using Stockpile.Domain.Entities.Catalogue;
using Stockpile.Domain.Entities.Identity;
using Stockpile.Domain.Entities.Procurement;
using Stockpile.Domain.Enums;
using Stockpile.Infrastructure.Persistence;
using Stockpile.IntegrationTests.Testing;

namespace Stockpile.IntegrationTests.Procurement;

/// <summary>
/// RFQ over real HTTP (business-flows §3.8, api.md §2.6): a buyer creates a
/// draft, submits it, and the matching pipeline dispatches it to the active
/// suppliers that cover the requested departments. Ownership is organisational —
/// a first-time buyer's company is provisioned on the same call — and another
/// buyer's RFQ answers a BOLA 404 (api.md §1.6).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class RfqApiTests
{
    private const string Password = "correct-horse-battery-staple";
    private const string ApiBase = "/api/v1";

    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    private readonly StockpileApiFactory _factory;

    /// <summary>Shares the one API host (single migrate + seed of Stockpile_Test).</summary>
    public RfqApiTests(StockpileApiFactory factory) => _factory = factory;

    private static string NewEmail(string prefix) => $"{prefix}.{Guid.NewGuid():N}@test.local";

    [Fact]
    public async Task Submit_matches_active_suppliers_in_the_department_and_dispatches()
    {
        // A supplier covering `hardware` gives the matching pipeline a
        // deterministic target beyond whatever the catalogue seed already has.
        string supplierSlug = await RegisterSupplierAsync("Lanka Rfq Timber (Pvt) Ltd", "hardware");

        var (buyer, token) = await RegisterBuyerAsync();
        Guid rfqId;
        using (buyer)
        {
            RfqResponse body = await CreateAndSubmitAsync(
                buyer, token, categorySlug: "hardware", companyName: "Buildwell Projects (Pvt) Ltd");
            rfqId = Guid.Parse(body.Id);

            Assert.Equal("Dispatched", body.Status);
            Assert.StartsWith("RFQ-", body.ReferenceCode, StringComparison.Ordinal);
            Assert.Single(body.Items);
            Assert.NotNull(body.SubmittedAt);
            Assert.Contains(body.Matches, match => match.SupplierSlug == supplierSlug);

            // The buyer's own read surfaces agree with the create response.
            using var detail = await buyer.GetAsync($"{ApiBase}/rfqs/{body.Id}");
            Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
            var detailBody = await detail.Content.ReadFromJsonAsync<RfqResponse>();
            Assert.Equal(body.Id, detailBody!.Id);
            Assert.Equal("Dispatched", detailBody.Status);

            using var list = await buyer.GetAsync($"{ApiBase}/me/rfqs");
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            var mine = await list.Content.ReadFromJsonAsync<List<RfqResponse>>();
            Assert.Contains(mine!, rfq => rfq.Id == body.Id);
        }

        // One submit persisted the whole aggregate: the RFQ owned by a
        // BuyerCompany organisation, its line items, its matches, and one
        // history row per transition (Draft → Submitted → Matching → Dispatched).
        using IServiceScope scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StockpileDbContext>();
        Rfq rfq = await db.Rfqs.SingleAsync(row => row.PublicId == rfqId);
        Assert.Equal(RFQStatus.Dispatched, rfq.Status);
        Organization organization = await db.Organizations.SingleAsync(row => row.PublicId == rfq.OrganizationId);
        Assert.Equal(OrganizationType.BuyerCompany, organization.Type);
        Assert.Equal(1, await db.RfqItems.CountAsync(item => item.RfqId == rfq.PublicId));
        Assert.True(await db.RfqMatches.AnyAsync(match => match.RfqId == rfq.PublicId));
        List<RFQStatus> transitions = await db.RfqStatusHistories
            .Where(history => history.RfqId == rfq.PublicId)
            .OrderBy(history => history.OccurredAt)
            .Select(history => history.ToState)
            .ToListAsync();
        Assert.Equal(
            new[] { RFQStatus.Submitted, RFQStatus.Matching, RFQStatus.Dispatched },
            transitions);
    }

    [Fact]
    public async Task A_brand_new_buyer_without_company_details_is_422_on_the_company_field()
    {
        var (buyer, token) = await RegisterBuyerAsync();
        using (buyer)
        {
            var payload = new CreateRfqPayload(
                Title, Company: null, "Colombo 03", SubstitutionsAllowed: true, SpecialInstructions: null,
                ExpiryDays: 14, [new ItemPayload("Marine plywood 12mm sheets", 25, "hardware", "each", null)]);

            using var response = await PostAsJsonAsync(buyer, $"{ApiBase}/rfqs", payload, token);

            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            var envelope = await response.Content.ReadFromJsonAsync<ErrorEnvelope>();
            Assert.Equal(ErrorCodes.ValidationFailed, envelope!.Code);
            Assert.Equal("company", Assert.Single(envelope.FieldErrors!).Field);
        }
    }

    [Fact]
    public async Task A_line_item_in_an_unknown_department_is_422_with_a_field_error()
    {
        var (buyer, token) = await RegisterBuyerAsync();
        using (buyer)
        {
            var payload = new CreateRfqPayload(
                Title, new CompanyPayload("Buildwell Projects (Pvt) Ltd", "PV-9", null),
                "Colombo 03", SubstitutionsAllowed: true, SpecialInstructions: null, ExpiryDays: 14,
                [new ItemPayload("Marine plywood 12mm sheets", 25, "not-a-real-department", "each", null)]);

            using var response = await PostAsJsonAsync(buyer, $"{ApiBase}/rfqs", payload, token);

            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            var envelope = await response.Content.ReadFromJsonAsync<ErrorEnvelope>();
            Assert.Equal(ErrorCodes.ValidationFailed, envelope!.Code);
            Assert.Equal("items[0].categorySlug", Assert.Single(envelope.FieldErrors!).Field);
        }
    }

    [Fact]
    public async Task Submitting_an_rfq_that_is_not_a_draft_is_409()
    {
        var (buyer, token) = await RegisterBuyerAsync();
        using (buyer)
        {
            RfqResponse submitted = await CreateAndSubmitAsync(
                buyer, token, categorySlug: "hardware", companyName: "Sunrise Traders (Pvt) Ltd");

            using var again = await PostAsJsonAsync(
                buyer,
                $"{ApiBase}/rfqs/{submitted.Id}/submit",
                payload: new { },
                token,
                idempotencyKey: Guid.NewGuid().ToString());

            Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
            var envelope = await again.Content.ReadFromJsonAsync<ErrorEnvelope>();
            Assert.Equal(ErrorCodes.RfqNotSubmittable, envelope!.Code);
        }
    }

    [Fact]
    public async Task Submit_without_idempotency_key_is_400()
    {
        var (buyer, token) = await RegisterBuyerAsync();
        using (buyer)
        {
            using var created = await PostAsJsonAsync(
                buyer, $"{ApiBase}/rfqs", Payload("hardware", "Keyless Projects (Pvt) Ltd"), token);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var draft = await created.Content.ReadFromJsonAsync<RfqResponse>();

            using var submitted = await PostAsJsonAsync(
                buyer, $"{ApiBase}/rfqs/{draft!.Id}/submit", payload: new { }, token);

            Assert.Equal(HttpStatusCode.BadRequest, submitted.StatusCode);
            var envelope = await submitted.Content.ReadFromJsonAsync<ErrorEnvelope>();
            Assert.Equal(ErrorCodes.IdempotencyKeyRequired, envelope!.Code);
        }
    }

    [Fact]
    public async Task Submit_replay_with_same_idempotency_key_does_not_re_dispatch()
    {
        var (buyer, token) = await RegisterBuyerAsync();
        Guid rfqId;
        int matchCountAfterFirst;
        using (buyer)
        {
            using var created = await PostAsJsonAsync(
                buyer, $"{ApiBase}/rfqs", Payload("hardware", "Replay Projects (Pvt) Ltd"), token);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var draft = await created.Content.ReadFromJsonAsync<RfqResponse>();
            rfqId = Guid.Parse(draft!.Id);
            string key = Guid.NewGuid().ToString();

            using var first = await PostAsJsonAsync(
                buyer,
                $"{ApiBase}/rfqs/{draft.Id}/submit",
                payload: new { },
                token,
                idempotencyKey: key);
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            Assert.False(first.Headers.Contains("Idempotency-Replayed"));
            var body = await first.Content.ReadFromJsonAsync<RfqResponse>();
            Assert.Equal("Dispatched", body!.Status);
            matchCountAfterFirst = body.Matches.Count;

            using var replay = await PostAsJsonAsync(
                buyer,
                $"{ApiBase}/rfqs/{draft.Id}/submit",
                payload: new { },
                token,
                idempotencyKey: key);
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
            Assert.True(replay.Headers.TryGetValues("Idempotency-Replayed", out var replayed));
            Assert.Equal("true", Assert.Single(replayed));
            var replayBody = await replay.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(body.Id, replayBody.GetProperty("reference").GetString());
            Assert.True(replayBody.GetProperty("replayed").GetBoolean());
        }

        using IServiceScope scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StockpileDbContext>();
        // Replay must not create a second match set or history chain.
        Assert.Equal(matchCountAfterFirst, await db.RfqMatches.CountAsync(match => match.RfqId == rfqId));
        Assert.Equal(3, await db.RfqStatusHistories.CountAsync(history => history.RfqId == rfqId));
    }

    [Fact]
    public async Task Another_buyers_rfq_answers_404_never_leaking_existence()
    {
        var (owner, ownerToken) = await RegisterBuyerAsync();
        RfqResponse rfq;
        using (owner)
        {
            rfq = await CreateAndSubmitAsync(
                owner, ownerToken, categorySlug: "hardware", companyName: "Northgate Builders (Pvt) Ltd");
        }

        var (stranger, _) = await RegisterBuyerAsync();
        using (stranger)
        {
            using var response = await stranger.GetAsync($"{ApiBase}/rfqs/{rfq.Id}");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            var envelope = await response.Content.ReadFromJsonAsync<ErrorEnvelope>();
            Assert.Equal(ErrorCodes.ResourceNotFound, envelope!.Code);

            using var list = await stranger.GetAsync($"{ApiBase}/me/rfqs");
            var mine = await list.Content.ReadFromJsonAsync<List<RfqResponse>>();
            Assert.DoesNotContain(mine!, item => item.Id == rfq.Id);
        }
    }

    // ---- Helpers -------------------------------------------------------------

    private const string Title = "Structural timber for site 4";

    private static CreateRfqPayload Payload(string categorySlug, string companyName) => new(
        Title,
        new CompanyPayload(companyName, "PV-12345", "https://buildwell.example"),
        "Colombo 03, Sri Lanka",
        SubstitutionsAllowed: true,
        SpecialInstructions: "Deliver within two weeks; BWP grade only.",
        ExpiryDays: 14,
        [new ItemPayload("Marine plywood 12mm sheets, 8x4", 25, categorySlug, "each", "BWP grade, moisture resistant")]);

    private static async Task<RfqResponse> CreateAndSubmitAsync(
        HttpClient buyer, string token, string categorySlug, string companyName)
    {
        using var created = await PostAsJsonAsync(
            buyer, $"{ApiBase}/rfqs", Payload(categorySlug, companyName), token);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var draft = await created.Content.ReadFromJsonAsync<RfqResponse>();
        Assert.Equal("Draft", draft!.Status);

        using var submitted = await PostAsJsonAsync(
            buyer,
            $"{ApiBase}/rfqs/{draft.Id}/submit",
            payload: new { },
            token,
            idempotencyKey: Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
        return (await submitted.Content.ReadFromJsonAsync<RfqResponse>())!;
    }

    private static async Task<HttpResponseMessage> PostAsJsonAsync(
        HttpClient client,
        string route,
        object payload,
        string requestToken,
        string? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, route)
        {
            Content = JsonContent.Create(payload, options: Wire),
        };
        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        return await AntiforgeryTestSupport.SendUnsafeAsync(client, request, requestToken);
    }

    /// <summary>Registers a buyer and returns the signed-in client plus its authenticated request token.</summary>
    private async Task<(HttpClient Client, string RequestToken)> RegisterBuyerAsync()
    {
        HttpClient client = AntiforgeryTestSupport.HttpsClient(_factory);
        string acquired = await AntiforgeryTestSupport.AcquireTokenAsync(client);
        string email = NewEmail("buyer");
        using (var register = await PostAsJsonAsync(
            client, $"{ApiBase}/auth/register", new { email, password = Password }, acquired))
        {
            Assert.Equal(HttpStatusCode.Created, register.StatusCode);
        }

        string loginToken = await AntiforgeryTestSupport.AcquireTokenAsync(client);
        using var login = await PostAsJsonAsync(
            client, $"{ApiBase}/auth/login", new { email, password = Password }, loginToken);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var body = await login.Content.ReadFromJsonAsync<LoginPayload>();
        return (client, body!.RequestToken!);
    }

    /// <summary>Registers a supplier covering one department and returns its public slug.</summary>
    private async Task<string> RegisterSupplierAsync(string companyName, string categorySlug)
    {
        using HttpClient client = AntiforgeryTestSupport.HttpsClient(_factory);
        string token = await AntiforgeryTestSupport.AcquireTokenAsync(client);
        string email = NewEmail("supplier");
        var payload = new
        {
            email,
            password = Password,
            contactFirstName = "Nimal",
            contactLastName = "Perera",
            phone = "+94771234567",
            companyName,
            registrationNumber = "PV-77777",
            website = "https://lanka-rfq.example",
            categories = new[] { categorySlug },
        };

        using var response = await PostAsJsonAsync(client, $"{ApiBase}/auth/register/supplier", payload, token);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using IServiceScope scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StockpileDbContext>();
        return await db.Suppliers
            .Where(supplier => supplier.DisplayName == companyName)
            .OrderByDescending(supplier => supplier.CreatedAt)
            .Select(supplier => supplier.Slug)
            .FirstAsync();
    }

    // ---- Wire shapes ---------------------------------------------------------

    private sealed record CreateRfqPayload(
        string Title,
        CompanyPayload? Company,
        string? DeliveryLocationText,
        bool SubstitutionsAllowed,
        string? SpecialInstructions,
        int? ExpiryDays,
        IReadOnlyList<ItemPayload> Items);

    private sealed record CompanyPayload(string Name, string? RegistrationNumber, string? Website);

    private sealed record ItemPayload(
        string Description, decimal Quantity, string? CategorySlug, string? UnitOfMeasureCode, string? Specification);

    private sealed record LoginPayload(string? RequestToken, object? User);
}
