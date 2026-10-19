using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Stockpile.Contracts.Errors;
using Stockpile.Contracts.Procurement;
using Stockpile.Infrastructure.Persistence;
using Stockpile.IntegrationTests.Testing;

namespace Stockpile.IntegrationTests.Procurement;

/// <summary>
/// Supplier quotations over real HTTP (api.md §2.7, business-flows §7):
/// creating a quote, computing totals, enforcing coverage and the machine's
/// guards, and BOLA/scoping (one supplier cannot quote another's lead).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class QuoteApiTests
{
    private const string Password = "correct-horse-battery-staple";
    private const string ApiBase = "/api/v1";
    private const string Department = "electrical";

    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    private readonly StockpileApiFactory _factory;

    public QuoteApiTests(StockpileApiFactory factory) => _factory = factory;

    private static string NewEmail(string prefix) => $"{prefix}.{Guid.NewGuid():N}@test.local";

    [Fact]
    public async Task Anonymous_is_401_and_buyer_without_capability_answers_403()
    {
        HttpClient anonymous = AntiforgeryTestSupport.HttpsClient(_factory);
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/quotes")
        {
            Content = JsonContent.Create(new QuotePayload
            {
                RfqMatchId = Guid.NewGuid().ToString(),
                Coverage = "Full",
                Currency = "LKR",
                TaxState = "TaxExcluded",
                ValidityDays = 14,
                Items = new List<QuoteItemPayload>
                {
                    new() { RfqItemId = Guid.NewGuid().ToString(), Quantity = 1, UnitPrice = 100m },
                },
            }, options: Wire),
        };
        req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using HttpResponseMessage anon = await AntiforgeryTestSupport.SendUnsafeAsync(anonymous, req, await AntiforgeryTestSupport.AcquireTokenAsync(anonymous));
        Assert.Equal(HttpStatusCode.Unauthorized, anon.StatusCode);

        var (buyer, token) = await SignInAsBuyerAsync();
        using (buyer)
        {
            using var breq = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/quotes")
            {
                Content = JsonContent.Create(new QuotePayload
                {
                    RfqMatchId = Guid.NewGuid().ToString(),
                    Coverage = "Full",
                    Currency = "LKR",
                    TaxState = "TaxExcluded",
                    ValidityDays = 14,
                    Items = new List<QuoteItemPayload>
                    {
                        new() { RfqItemId = Guid.NewGuid().ToString(), Quantity = 1, UnitPrice = 100m },
                    },
                }, options: Wire),
            };
            breq.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
            using HttpResponseMessage bres = await AntiforgeryTestSupport.SendUnsafeAsync(buyer, breq, token);
            Assert.Equal(HttpStatusCode.Forbidden, bres.StatusCode);
        }
    }

    [Fact]
    public async Task Happy_path_submits_quote_and_computes_totals()
    {
        var (supplier, token, _) = await SignInAsSupplierAsync();
        using (supplier)
        {
            var (buyer, buyerToken) = await SignInAsBuyerAsync();
            RfqResponse rfq;
            using (buyer)
            {
                rfq = await SubmitRfqAsync(buyer, buyerToken, "Cables for a switchboard");
            }

            SupplierLeadDetailPayload lead = await GetLeadDetailAsync(supplier, await SingleLeadIdAsync(supplier, rfq.ReferenceCode));
            Assert.True(lead.Lines.Count >= 2);

            QuotePayloadResponse quote = await SubmitQuoteAsync(
                supplier,
                token,
                lead,
                new QuotePayload
                {
                    RfqMatchId = lead.LeadId,
                    Coverage = "Full",
                    Currency = "LKR",
                    TaxState = "TaxExcluded",
                    ValidityDays = 14,
                    DeliveryCost = 1250m,
                    DiscountPercent = 2.5m,
                    PaymentTerms = "30 days",
                    DeliveryTerms = "Delivered to site",
                    SupplierNote = "Ready to supply",
                    Items = lead.Lines.Select(l => new QuoteItemPayload
                    {
                        RfqItemId = l.RfqItemId,
                        Quantity = l.Quantity,
                        UnitPrice = 100m,
                    }).ToList(),
                });
            Assert.Equal("Submitted", quote.Status);
            Assert.StartsWith("QT-", quote.QuoteNumber, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(1, quote.RevisionNumber);
            Assert.Equal("LKR", quote.Totals.Currency);
            Assert.Equal(quote.Coverage.RequestedLines, quote.Coverage.QuotedLines);
            Assert.Empty(quote.Coverage.MissingLineNumbers);
        }
    }

    [Fact]
    public async Task Idempotency_header_required()
    {
        var (supplier, token, _) = await SignInAsSupplierAsync();
        using (supplier)
        {
            var (buyer, buyerToken) = await SignInAsBuyerAsync();
            RfqResponse rfq;
            using (buyer)
            {
                rfq = await SubmitRfqAsync(buyer, buyerToken, "Trunking");
            }
            SupplierLeadDetailPayload lead = await GetLeadDetailAsync(supplier, await SingleLeadIdAsync(supplier, rfq.ReferenceCode));
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/quotes")
            {
                Content = JsonContent.Create(new QuotePayload
                {
                    RfqMatchId = lead.LeadId,
                    Coverage = "Full",
                    Currency = "LKR",
                    TaxState = "TaxExcluded",
                    ValidityDays = 14,
                    Items = lead.Lines.Take(1).Select(l => new QuoteItemPayload { RfqItemId = l.RfqItemId, Quantity = l.Quantity, UnitPrice = 50m }).ToList(),
                }, options: Wire),
            };
            using HttpResponseMessage res = await AntiforgeryTestSupport.SendUnsafeAsync(supplier, req, token);
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
            var env = await res.Content.ReadFromJsonAsync<ErrorEnvelope>(Wire);
            Assert.Equal(ErrorCodes.IdempotencyKeyRequired, env!.Code);
        }
    }

    [Fact]
    public async Task Duplicate_quote_returns_409()
    {
        var (supplier, token, _) = await SignInAsSupplierAsync();
        using (supplier)
        {
            var (buyer, buyerToken) = await SignInAsBuyerAsync();
            RfqResponse rfq;
            using (buyer)
            {
                rfq = await SubmitRfqAsync(buyer, buyerToken, "Conduit");
            }
            SupplierLeadDetailPayload lead = await GetLeadDetailAsync(supplier, await SingleLeadIdAsync(supplier, rfq.ReferenceCode));
            await SubmitQuoteAsync(supplier, token, lead, BuildFullQuote(lead));
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/quotes")
            {
                Content = JsonContent.Create(BuildFullQuote(lead), options: Wire),
            };
            req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
            using HttpResponseMessage res = await AntiforgeryTestSupport.SendUnsafeAsync(supplier, req, token);
            Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
            var env = await res.Content.ReadFromJsonAsync<ErrorEnvelope>(Wire);
            Assert.Equal(ErrorCodes.QuoteExistsForRfq, env!.Code);
        }
    }

    [Fact]
    public async Task Substitution_not_permitted_returns_422()
    {
        var (supplier, token, _) = await SignInAsSupplierAsync();
        using (supplier)
        {
            var (buyer, buyerToken) = await SignInAsBuyerAsync();
            RfqResponse rfq;
            using (buyer)
            {
                rfq = await SubmitRfqAsync(buyer, buyerToken, "Panel board", substitutionsAllowed: false);
            }
            SupplierLeadDetailPayload lead = await GetLeadDetailAsync(supplier, await SingleLeadIdAsync(supplier, rfq.ReferenceCode));
            var payload = BuildFullQuote(lead);
            payload.Items[0] = payload.Items[0] with { SubstitutionNote = "Equivalent brand OK" };
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/quotes")
            {
                Content = JsonContent.Create(payload, options: Wire),
            };
            req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
            using HttpResponseMessage res = await AntiforgeryTestSupport.SendUnsafeAsync(supplier, req, token);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
            var env = await res.Content.ReadFromJsonAsync<ErrorEnvelope>(Wire);
            Assert.Equal(ErrorCodes.SubstitutionNotPermitted, env!.Code);
            Assert.Equal("items[0].substitutionNote", env.FieldErrors![0].Field);
        }
    }

    [Fact]
    public async Task Full_but_missing_line_returns_422()
    {
        var (supplier, token, _) = await SignInAsSupplierAsync();
        using (supplier)
        {
            var (buyer, buyerToken) = await SignInAsBuyerAsync();
            RfqResponse rfq;
            using (buyer)
            {
                rfq = await SubmitRfqAsync(buyer, buyerToken, "DB board");
            }
            SupplierLeadDetailPayload lead = await GetLeadDetailAsync(supplier, await SingleLeadIdAsync(supplier, rfq.ReferenceCode));
            var payload = new QuotePayload
            {
                RfqMatchId = lead.LeadId,
                Coverage = "Full",
                Currency = "LKR",
                TaxState = "TaxExcluded",
                ValidityDays = 14,
                Items = new List<QuoteItemPayload> { lead.Lines.Take(1).Select(l => new QuoteItemPayload { RfqItemId = l.RfqItemId, Quantity = l.Quantity, UnitPrice = 10m }).Single() },
            };
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/quotes")
            {
                Content = JsonContent.Create(payload, options: Wire),
            };
            req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
            using HttpResponseMessage res = await AntiforgeryTestSupport.SendUnsafeAsync(supplier, req, token);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
            var env = await res.Content.ReadFromJsonAsync<ErrorEnvelope>(Wire);
            Assert.Equal(ErrorCodes.ValidationFailed, env!.Code);
            Assert.Contains(env.FieldErrors!, e => e.Field == "coverage");
        }
    }

    [Fact]
    public async Task Partial_coverage_returns_missing_line_numbers()
    {
        var (supplier, token, _) = await SignInAsSupplierAsync();
        using (supplier)
        {
            var (buyer, buyerToken) = await SignInAsBuyerAsync();
            RfqResponse rfq;
            using (buyer)
            {
                rfq = await SubmitRfqAsync(buyer, buyerToken, "Cable tray");
            }
            SupplierLeadDetailPayload lead = await GetLeadDetailAsync(supplier, await SingleLeadIdAsync(supplier, rfq.ReferenceCode));
            var payload = new QuotePayload
            {
                RfqMatchId = lead.LeadId,
                Coverage = "Partial",
                Currency = "LKR",
                TaxState = "TaxExcluded",
                ValidityDays = 14,
                Items = new List<QuoteItemPayload> { lead.Lines.Take(1).Select(l => new QuoteItemPayload { RfqItemId = l.RfqItemId, Quantity = l.Quantity, UnitPrice = 10m }).Single() },
            };
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/quotes")
            {
                Content = JsonContent.Create(payload, options: Wire),
            };
            req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
            using HttpResponseMessage res = await AntiforgeryTestSupport.SendUnsafeAsync(supplier, req, token);
            Assert.Equal(HttpStatusCode.Created, res.StatusCode);
            var quote = await res.Content.ReadFromJsonAsync<QuotePayloadResponse>(Wire);
            Assert.Equal(1, quote!.Coverage.QuotedLines);
            Assert.Equal(lead.Lines.Count - 1, quote.Coverage.MissingLineNumbers.Count);
        }
    }

    [Fact]
    public async Task Expired_rfq_returns_409()
    {
        var (supplier, token, _) = await SignInAsSupplierAsync();
        using (supplier)
        {
            var (buyer, buyerToken) = await SignInAsBuyerAsync();
            RfqResponse rfq;
            using (buyer)
            {
                rfq = await SubmitRfqAsync(buyer, buyerToken, "Distribution board");
            }
            await WithDbAsync(async db =>
            {
                var rfqRow = await db.Rfqs.SingleAsync(r => r.ReferenceCode == rfq.ReferenceCode);
                rfqRow.ExpiryAt = DateTime.UtcNow.AddMinutes(-1);
                await db.SaveChangesAsync();
            });
            SupplierLeadDetailPayload lead = await GetLeadDetailAsync(supplier, await SingleLeadIdAsync(supplier, rfq.ReferenceCode));
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/quotes")
            {
                Content = JsonContent.Create(BuildFullQuote(lead), options: Wire),
            };
            req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
            using HttpResponseMessage res = await AntiforgeryTestSupport.SendUnsafeAsync(supplier, req, token);
            Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
            var env = await res.Content.ReadFromJsonAsync<ErrorEnvelope>(Wire);
            Assert.Equal(ErrorCodes.RfqExpired, env!.Code);
        }
    }

    // Helpers
    private static QuotePayload BuildFullQuote(SupplierLeadDetailPayload lead)
        => new()
        {
            RfqMatchId = lead.LeadId,
            Coverage = "Full",
            Currency = "LKR",
            TaxState = "TaxExcluded",
            ValidityDays = 14,
            Items = lead.Lines.Select(l => new QuoteItemPayload
            {
                RfqItemId = l.RfqItemId,
                Quantity = l.Quantity,
                UnitPrice = 75m,
            }).ToList(),
        };

    private async Task<QuotePayloadResponse> SubmitQuoteAsync(HttpClient supplier, string token, SupplierLeadDetailPayload lead, QuotePayload payload)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/quotes")
        {
            Content = JsonContent.Create(payload, options: Wire),
        };
        req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using HttpResponseMessage res = await AntiforgeryTestSupport.SendUnsafeAsync(supplier, req, token);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var q = await res.Content.ReadFromJsonAsync<QuotePayloadResponse>(Wire);
        Assert.NotNull(q);
        return q!;
    }

    // Reuse the same helpers style as leads test - include here to be self-contained
    private async Task<(HttpClient Client, string Token, Guid SupplierId)> SignInAsSupplierAsync()
    {
        HttpClient client = AntiforgeryTestSupport.HttpsClient(_factory);
        string email = NewEmail("supplier");
        string company = $"Fixture {Guid.NewGuid():N} (Pvt) Ltd";
        string token = await AntiforgeryTestSupport.AcquireTokenAsync(client);
        using (HttpResponseMessage registered = await PostAsync(client, $"{ApiBase}/auth/register/supplier", new
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
        }, token))
        {
            Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        }
        Guid supplierId;
        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StockpileDbContext>();
            supplierId = await db.Suppliers.Where(s => s.DisplayName == company).Select(s => s.PublicId).SingleAsync();
        }
        token = await AntiforgeryTestSupport.AcquireTokenAsync(client);
        using (HttpResponseMessage login = await PostAsync(client, $"{ApiBase}/auth/login", new { email, password = Password }, token))
        {
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        }
        return (client, await AntiforgeryTestSupport.AcquireTokenAsync(client), supplierId);
    }

    private async Task<(HttpClient Client, string Token)> SignInAsBuyerAsync()
    {
        HttpClient client = AntiforgeryTestSupport.HttpsClient(_factory);
        string email = NewEmail("buyer");
        string token = await AntiforgeryTestSupport.AcquireTokenAsync(client);
        using (HttpResponseMessage registered = await PostAsync(client, $"{ApiBase}/auth/register", new { email, password = Password }, token))
        {
            Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        }
        token = await AntiforgeryTestSupport.AcquireTokenAsync(client);
        using (HttpResponseMessage login = await PostAsync(client, $"{ApiBase}/auth/login", new { email, password = Password }, token))
        {
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        }
        return (client, await AntiforgeryTestSupport.AcquireTokenAsync(client));
    }

    private static async Task<RfqResponse> SubmitRfqAsync(HttpClient buyer, string token, string title, bool substitutionsAllowed = false)
    {
        var payload = new
        {
            title,
            company = new { name = "Buildwell (Pvt) Ltd", registrationNumber = "PV-12345", website = "https://buildwell.example" },
            deliveryLocationText = "Colombo 03, Sri Lanka",
            substitutionsAllowed,
            specialInstructions = "Standard spec",
            expiryDays = 21,
            items = new[]
            {
                new { description = "Item A", quantity = 10m, categorySlug = Department, unitOfMeasureCode = "each", specification = "Spec A" },
                new { description = "Item B", quantity = 20m, categorySlug = Department, unitOfMeasureCode = "each", specification = (string?)null },
            },
        };
        using HttpResponseMessage created = await PostAsync(buyer, $"{ApiBase}/rfqs", payload, token);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var draft = await created.Content.ReadFromJsonAsync<RfqResponse>(Wire);
        string submitToken = await AntiforgeryTestSupport.AcquireTokenAsync(buyer);
        using HttpResponseMessage submitted = await PostAsync(buyer, $"{ApiBase}/rfqs/{draft!.Id}/submit", new { }, submitToken);
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
        return (await submitted.Content.ReadFromJsonAsync<RfqResponse>(Wire))!;
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string route, object payload, string requestToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, route)
        {
            Content = JsonContent.Create(payload, options: Wire),
        };
        return await AntiforgeryTestSupport.SendUnsafeAsync(client, request, requestToken);
    }

    private async Task<SupplierLeadPagePayload> ListAsync(HttpClient client, string query = "")
    {
        using HttpResponseMessage response = await client.GetAsync($"{ApiBase}/suppliers/me/leads{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SupplierLeadPagePayload>(Wire))!;
    }

    private async Task<string> SingleLeadIdAsync(HttpClient client, string referenceCode)
    {
        var page = await ListAsync(client, "?limit=100");
        var lead = Assert.Single(page.Items, l => l.ReferenceCode == referenceCode);
        return lead.LeadId;
    }

    private async Task<SupplierLeadDetailPayload> GetLeadDetailAsync(HttpClient client, string leadId)
    {
        using HttpResponseMessage res = await client.GetAsync($"{ApiBase}/suppliers/me/leads/{leadId}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<SupplierLeadDetailPayload>(Wire))!;
    }

    private async Task WithDbAsync(Func<StockpileDbContext, Task> work)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StockpileDbContext>();
        await work(db);
    }

    // Wire
    private sealed record QuotePayload
    {
        public string RfqMatchId { get; init; } = string.Empty;
        public string Coverage { get; init; } = string.Empty;
        public string Currency { get; init; } = string.Empty;
        public int? LeadTimeDays { get; init; }
        public int? ValidityDays { get; init; }
        public string? PaymentTerms { get; init; }
        public string? DeliveryTerms { get; init; }
        public decimal? DeliveryCost { get; init; }
        public string TaxState { get; init; } = string.Empty;
        public decimal? DiscountPercent { get; init; }
        public string? PackSizeNote { get; init; }
        public string? MoqNote { get; init; }
        public string? TechnicalNotes { get; init; }
        public string? SupplierNote { get; init; }
        public List<QuoteItemPayload> Items { get; init; } = new();
    }

    private sealed record QuoteItemPayload
    {
        public string RfqItemId { get; init; } = string.Empty;
        public string? ProductId { get; init; }
        public decimal Quantity { get; init; }
        public decimal UnitPrice { get; init; }
        public string? SubstitutionNote { get; init; }
    }

    private sealed record QuotePayloadResponse(
        string PublicId, string QuoteNumber, string Status, int RevisionNumber,
        QuoteTotalsPayload Totals, QuoteCoveragePayload Coverage);
    private sealed record QuoteTotalsPayload(decimal Subtotal, decimal DiscountTotal, decimal DeliveryCost, decimal TaxAmount, decimal GrandTotal, string Currency);
    private sealed record QuoteCoveragePayload(int RequestedLines, int QuotedLines, List<int> MissingLineNumbers);

    private sealed record RfqResponse(string Id, string ReferenceCode, string Status);
    private sealed record SupplierLeadPagePayload(List<SupplierLeadPayload> Items, string? NextCursor, bool HasMore);
    private sealed record SupplierLeadPayload(string LeadId, string Status, string ReferenceCode, string Title, string? ReceivedAt, string? RespondBy, string? RequestedDeliveryAt, int LineCount, string? ViewedAt, string? DeclineReason);
    private sealed record SupplierLeadDetailPayload(string LeadId, string Status, string RfqId, string ReferenceCode, string Title, string? DeliveryLocationText, string? RequestedDeliveryAt, string? RespondBy, bool SubstitutionsAllowed, string? BuyerNotes, List<SupplierLeadLinePayload> Lines, string? NotifiedAt, string? ViewedAt, string? RespondedAt, string? DeclineReason);
    private sealed record SupplierLeadLinePayload(string RfqItemId, int LineNumber, string Description, string? Specification, decimal Quantity, string? UnitCode, string? ExpectedAt, string? Notes);
}