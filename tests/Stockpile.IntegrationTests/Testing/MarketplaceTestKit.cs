using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Stockpile.Contracts.Errors;
using Stockpile.Contracts.Procurement;
using Stockpile.Domain.Entities.Identity;
using Stockpile.Infrastructure.Persistence;

namespace Stockpile.IntegrationTests.Testing;

/// <summary>
/// Shared scaffolding for the marketplace suites (quotes, orders, subscriptions):
/// registers real buyer / supplier / operator accounts over HTTP, drives an RFQ
/// to a supplier's lead and a quote, and sends every unsafe request with a fresh
/// antiforgery token and — unless a test supplies its own — a fresh
/// <c>Idempotency-Key</c>. No fixture data is invented: every account, RFQ and
/// quote is created through the same endpoints a real client uses.
/// </summary>
public sealed class MarketplaceTestKit(StockpileApiFactory factory)
{
    public const string Password = "correct-horse-battery-staple";
    public const string ApiBase = "/api/v1";

    private const string Department = "electrical";

    public static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    public StockpileApiFactory Factory => factory;

    /// <summary>A signed-in test account: the cookie-holding client plus, for a supplier, its company id.</summary>
    public sealed record Actor(HttpClient Client, string Email, Guid? SupplierId);

    /// <summary>A response read to the end, so the underlying message can be disposed at once.</summary>
    public sealed record ApiResult(HttpStatusCode Status, string Body, bool Replayed)
    {
        public JsonElement Json => JsonDocument.Parse(Body).RootElement.Clone();

        /// <summary>The error envelope's <c>code</c>, or null for a success body.</summary>
        public string? Code => Status >= HttpStatusCode.BadRequest
            ? JsonSerializer.Deserialize<ErrorEnvelope>(Body, Wire)?.Code
            : null;

        public T As<T>() => JsonSerializer.Deserialize<T>(Body, Wire)
            ?? throw new InvalidOperationException($"Response body was empty for {typeof(T).Name}: {Body}");

        public string Str(string property) => Json.GetProperty(property).GetString()!;
    }

    /// <summary>One RFQ line as the supplier's lead shows it.</summary>
    public sealed record Line(Guid RfqItemId, decimal Quantity);

    /// <summary>The supplier's view of one matched RFQ.</summary>
    public sealed record Lead(Guid LeadId, IReadOnlyList<Line> Lines);

    /// <summary>A buyer, a supplier, and one quote the supplier has submitted on the buyer's RFQ.</summary>
    public sealed record Scenario(
        Actor Buyer, Actor Supplier, string RfqId, string Reference, Lead Lead, QuoteResponse Quote);

    public static string NewEmail(string prefix) => $"{prefix}.{Guid.NewGuid():N}@test.local";

    // ---- Accounts ---------------------------------------------------------

    public async Task<Actor> RegisterSupplierAsync()
    {
        HttpClient client = AntiforgeryTestSupport.HttpsClient(factory);
        string email = NewEmail("supplier");
        string company = $"Fixture {Guid.NewGuid():N} (Pvt) Ltd";
        ApiResult registered = await SendAnonymousAsync(client, $"{ApiBase}/auth/register/supplier", new
        {
            email,
            password = Password,
            contactFirstName = "A",
            contactLastName = "B",
            phone = "+94771234567",
            companyName = company,
            registrationNumber = "PV-88888",
            website = "https://example.com",
            categories = new[] { Department },
        });
        Assert.Equal(HttpStatusCode.Created, registered.Status);

        Guid supplierId = await WithDbAsync(db => db.Suppliers
            .Where(supplier => supplier.DisplayName == company)
            .Select(supplier => supplier.PublicId)
            .SingleAsync());

        await LoginAsync(client, email);
        return new Actor(client, email, supplierId);
    }

    public async Task<Actor> RegisterBuyerAsync()
    {
        HttpClient client = AntiforgeryTestSupport.HttpsClient(factory);
        string email = NewEmail("buyer");
        ApiResult registered = await SendAnonymousAsync(client, $"{ApiBase}/auth/register", new { email, password = Password });
        Assert.Equal(HttpStatusCode.Created, registered.Status);
        await LoginAsync(client, email);
        return new Actor(client, email, null);
    }

    /// <summary>A platform operator: a plain account granted a seeded platform role, then signed in.</summary>
    public async Task<Actor> RegisterOperatorAsync(string role)
    {
        HttpClient client = AntiforgeryTestSupport.HttpsClient(factory);
        string email = NewEmail("operator");
        ApiResult registered = await SendAnonymousAsync(client, $"{ApiBase}/auth/register", new { email, password = Password });
        Assert.Equal(HttpStatusCode.Created, registered.Status);

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            User user = await users.FindByEmailAsync(email)
                ?? throw new InvalidOperationException($"No user row for {email}.");
            IdentityResult result = await users.AddToRoleAsync(user, role);
            Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(error => error.Description)));
        }

        await LoginAsync(client, email);
        return new Actor(client, email, null);
    }

    private async Task LoginAsync(HttpClient client, string email)
    {
        ApiResult login = await SendAnonymousAsync(client, $"{ApiBase}/auth/login", new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, login.Status);
    }

    // ---- Requests ---------------------------------------------------------

    /// <summary>Sends a request that needs no identity (registration, login, webhooks) but still carries the double-submit token.</summary>
    public static async Task<ApiResult> SendAnonymousAsync(HttpClient client, string route, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, route)
        {
            Content = JsonContent.Create(body, options: Wire),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        string token = await AntiforgeryTestSupport.AcquireTokenAsync(client);
        return await ReadAsync(await AntiforgeryTestSupport.SendUnsafeAsync(client, request, token));
    }

    /// <summary>Sends an unsafe request as <paramref name="actor"/> with a fresh token and (by default) a fresh idempotency key.</summary>
    public static async Task<ApiResult> SendAsync(
        Actor actor, HttpMethod method, string route, object? body = null, string? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(method, route);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Wire);
        }

        request.Headers.Add("Idempotency-Key", idempotencyKey ?? Guid.NewGuid().ToString());
        string token = await AntiforgeryTestSupport.AcquireTokenAsync(actor.Client);
        return await ReadAsync(await AntiforgeryTestSupport.SendUnsafeAsync(actor.Client, request, token));
    }

    public static Task<ApiResult> PostAsync(Actor actor, string route, object? body = null, string? idempotencyKey = null)
        => SendAsync(actor, HttpMethod.Post, route, body ?? new { }, idempotencyKey);

    public static async Task<ApiResult> GetAsync(Actor actor, string route)
        => await ReadAsync(await actor.Client.GetAsync(route));

    private static async Task<ApiResult> ReadAsync(HttpResponseMessage response)
    {
        using (response)
        {
            return new ApiResult(
                response.StatusCode,
                await response.Content.ReadAsStringAsync(),
                response.Headers.Contains("Idempotency-Replayed"));
        }
    }

    // ---- RFQ → lead → quote -----------------------------------------------

    public static async Task<(string Id, string Reference)> SubmitRfqAsync(
        Actor buyer, string title, bool substitutionsAllowed = false, int quantity = 10)
    {
        ApiResult created = await PostAsync(buyer, $"{ApiBase}/rfqs", new
        {
            title,
            company = new { name = "Buildwell (Pvt) Ltd", registrationNumber = "PV-12345", website = "https://buildwell.example" },
            deliveryLocationText = "Colombo 03, Sri Lanka",
            substitutionsAllowed,
            specialInstructions = "Standard spec",
            expiryDays = 21,
            items = new[]
            {
                new { description = "Cable item A", quantity = (decimal)quantity, categorySlug = Department, unitOfMeasureCode = "each", specification = "Spec A" },
                new { description = "Cable item B", quantity = (decimal)(quantity * 2), categorySlug = Department, unitOfMeasureCode = "each", specification = (string?)null },
            },
        });
        Assert.Equal(HttpStatusCode.Created, created.Status);

        ApiResult submitted = await PostAsync(buyer, $"{ApiBase}/rfqs/{created.Str("id")}/submit");
        Assert.Equal(HttpStatusCode.OK, submitted.Status);
        return (submitted.Str("id"), submitted.Str("referenceCode"));
    }

    public static async Task<Lead> FindLeadAsync(Actor supplier, string reference)
    {
        ApiResult page = await GetAsync(supplier, $"{ApiBase}/suppliers/me/leads?limit=100");
        Assert.Equal(HttpStatusCode.OK, page.Status);
        JsonElement match = page.Json.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("referenceCode").GetString() == reference);
        string leadId = match.GetProperty("leadId").GetString()!;

        ApiResult detail = await GetAsync(supplier, $"{ApiBase}/suppliers/me/leads/{leadId}");
        Assert.Equal(HttpStatusCode.OK, detail.Status);
        List<Line> lines = [.. detail.Json.GetProperty("lines").EnumerateArray().Select(line => new Line(
            Guid.Parse(line.GetProperty("rfqItemId").GetString()!),
            line.GetProperty("quantity").GetDecimal()))];
        return new Lead(Guid.Parse(leadId), lines);
    }

    public static CreateQuoteRequest FullQuote(
        Lead lead, decimal unitPrice = 75m, int validityDays = 14, string? supplierNote = null, Guid? productId = null)
        => new(
            lead.LeadId,
            "Full",
            "LKR",
            LeadTimeDays: 7,
            ValidityDays: validityDays,
            PaymentTerms: "30 days",
            DeliveryTerms: "Delivered to site",
            DeliveryCost: 100m,
            TaxState: "TaxExcluded",
            DiscountPercent: null,
            PackSizeNote: null,
            MoqNote: null,
            TechnicalNotes: null,
            SupplierNote: supplierNote,
            [.. lead.Lines.Select((line, index) => new CreateQuoteItemRequest(
                line.RfqItemId, index == 0 ? productId : null, line.Quantity, unitPrice, null))]);

    public static ReviseQuoteRequest Revision(Lead lead, decimal unitPrice, int validityDays = 14, string? supplierNote = null)
        => new(
            "Full",
            "LKR",
            LeadTimeDays: 5,
            ValidityDays: validityDays,
            PaymentTerms: "30 days",
            DeliveryTerms: "Delivered to site",
            DeliveryCost: 100m,
            TaxState: "TaxExcluded",
            DiscountPercent: null,
            PackSizeNote: null,
            MoqNote: null,
            TechnicalNotes: null,
            SupplierNote: supplierNote,
            RevisionKind: null,
            [.. lead.Lines.Select(line => new CreateQuoteItemRequest(line.RfqItemId, null, line.Quantity, unitPrice, null))]);

    /// <summary>Buyer RFQ + a supplier's quote on it, end to end through the real endpoints.</summary>
    public async Task<Scenario> CreateQuoteAsync(
        Actor? supplier = null, Actor? buyer = null, string title = "Cables for a switchboard", decimal unitPrice = 75m,
        int validityDays = 14, Guid? productId = null)
    {
        supplier ??= await RegisterSupplierAsync();
        buyer ??= await RegisterBuyerAsync();
        (string rfqId, string reference) = await SubmitRfqAsync(buyer, title);
        Lead lead = await FindLeadAsync(supplier, reference);

        ApiResult quote = await PostAsync(
            supplier, $"{ApiBase}/quotes", FullQuote(lead, unitPrice, validityDays, productId: productId));
        Assert.Equal(HttpStatusCode.Created, quote.Status);
        return new Scenario(buyer, supplier, rfqId, reference, lead, quote.As<QuoteResponse>());
    }

    // ---- Database ---------------------------------------------------------

    public async Task<T> WithDbAsync<T>(Func<StockpileDbContext, Task<T>> work)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<StockpileDbContext>());
    }

    public async Task WithDbAsync(Func<StockpileDbContext, Task> work)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        await work(scope.ServiceProvider.GetRequiredService<StockpileDbContext>());
    }
}
