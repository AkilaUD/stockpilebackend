using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Stockpile.Contracts.Errors;
using Stockpile.Contracts.Procurement;
using Stockpile.Domain.Entities.Procurement;
using Stockpile.Infrastructure.Persistence;
using Stockpile.IntegrationTests.Testing;

namespace Stockpile.IntegrationTests.Suppliers;

/// <summary>
/// The supplier's buying-lead inbox over real HTTP (business-flows §6,
/// api.md §2.9): the matched RFQs a supplier was dispatched, opening one, and
/// declining one with a reason.
/// <para>
/// What this pins: the surface is authenticated and scoped to the caller's own
/// supplier — a foreign lead id answers one 404 on every route, never a 403 or a
/// hint that it exists (BOLA, api.md §1.6) — and two suppliers matched to the
/// SAME RFQ each hold their own lead. The lead view carries the requested lines
/// and the respond-by date and never the buyer's private data (§6 "not shown").
/// Opening a lead is recorded once (§11.2's declared
/// <c>Matched → Notified → Viewed</c> chain, because nothing in this phase
/// dispatches a notification) and moves it from the <c>new</c> view to
/// <c>follow_up</c>; declining requires a reason, moves it to <c>lost</c> and
/// stays readable. The feed is cursor-paginated over
/// <c>(arrival, leadId)</c>, so a page boundary that lands on a tie neither
/// repeats nor skips a row. A view that needs the quote subsystem this phase does
/// not have (<c>won</c>) answers empty rather than claim a win.
/// </para>
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class SupplierLeadsApiTests
{
    private const string Password = "correct-horse-battery-staple";
    private const string ApiBase = "/api/v1";
    private const string LeadsBase = $"{ApiBase}/suppliers/me/leads";

    /// <summary>
    /// A root department no other suite submits RFQs in, so the matching
    /// pipeline gives each test's supplier that test's leads and nothing else:
    /// every test registers its own supplier, and only RFQs created after that
    /// registration can match it.
    /// </summary>
    private const string Department = "electrical";

    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    private readonly StockpileApiFactory _factory;

    public SupplierLeadsApiTests(StockpileApiFactory factory) => _factory = factory;

    private static string NewEmail(string prefix) => $"{prefix}.{Guid.NewGuid():N}@test.local";

    // ---- The surface is authenticated and scoped ------------------------------

    [Fact]
    public async Task Anonymous_is_401_and_a_caller_without_a_supplier_answers_404_or_403()
    {
        using HttpClient anonymous = AntiforgeryTestSupport.HttpsClient(_factory);
        using (HttpResponseMessage listing = await anonymous.GetAsync(LeadsBase))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, listing.StatusCode);
            var envelope = await listing.Content.ReadFromJsonAsync<ErrorEnvelope>();
            Assert.Equal(ErrorCodes.Unauthenticated, envelope!.Code);
        }

        using (HttpResponseMessage detail = await anonymous.GetAsync($"{LeadsBase}/{Guid.NewGuid()}"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, detail.StatusCode);
        }

        // A buyer has no supplier: the reads answer the surface's one 404 — the
        // same answer a lead they do not own gets — and declining is refused on
        // the §11.2 capability before any lookup can leak existence.
        var (buyer, token) = await SignInAsBuyerAsync();
        using (buyer)
        {
            using (HttpResponseMessage listing = await buyer.GetAsync(LeadsBase))
            {
                Assert.Equal(HttpStatusCode.NotFound, listing.StatusCode);
                var envelope = await listing.Content.ReadFromJsonAsync<ErrorEnvelope>();
                Assert.Equal(ErrorCodes.ResourceNotFound, envelope!.Code);
            }

            using (HttpResponseMessage view = await PostAsync(buyer, $"{LeadsBase}/{Guid.NewGuid()}/view", new { }, token))
            {
                Assert.Equal(HttpStatusCode.NotFound, view.StatusCode);
            }

            using (HttpResponseMessage decline = await PostAsync(
                buyer, $"{LeadsBase}/{Guid.NewGuid()}/decline", new { reason = "Not our range." }, token))
            {
                Assert.Equal(HttpStatusCode.Forbidden, decline.StatusCode);
                var envelope = await decline.Content.ReadFromJsonAsync<ErrorEnvelope>();
                Assert.Equal(ErrorCodes.InsufficientCapability, envelope!.Code);
            }
        }
    }

    [Fact]
    public async Task Two_suppliers_matched_to_one_rfq_each_own_their_lead_and_never_see_the_others()
    {
        // Both are registered BEFORE the submit, so the matching pipeline
        // dispatches a lead to each: same RFQ, two different lead rows.
        var (first, firstToken, _) = await SignInAsSupplierAsync();
        var (second, _, _) = await SignInAsSupplierAsync();
        using (first)
        using (second)
        {
            var (buyer, buyerToken) = await SignInAsBuyerAsync();
            RfqResponse rfq;
            using (buyer)
            {
                rfq = await SubmitRfqAsync(buyer, buyerToken, "BOLA wiring and cable run");
            }

            SupplierLeadPayload firstLead = await SingleLeadAsync(first, rfq.ReferenceCode);
            SupplierLeadPayload secondLead = await SingleLeadAsync(second, rfq.ReferenceCode);

            Assert.NotEqual(firstLead.LeadId, secondLead.LeadId);
            Assert.Equal(firstLead.ReferenceCode, secondLead.ReferenceCode);

            // The other supplier's lead is not readable, not openable, not
            // declinable — and never appears in that supplier's own list.
            using (HttpResponseMessage detail = await first.GetAsync($"{LeadsBase}/{secondLead.LeadId}"))
            {
                Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);
            }

            using (HttpResponseMessage view = await PostAsync(
                first, $"{LeadsBase}/{secondLead.LeadId}/view", new { }, firstToken))
            {
                Assert.Equal(HttpStatusCode.NotFound, view.StatusCode);
            }

            using (HttpResponseMessage decline = await PostAsync(
                first, $"{LeadsBase}/{secondLead.LeadId}/decline", new { reason = "Not ours." }, firstToken))
            {
                Assert.Equal(HttpStatusCode.NotFound, decline.StatusCode);
            }

            SupplierLeadPagePayload mine = await ListAsync(first);
            Assert.DoesNotContain(mine.Items, lead => lead.LeadId == secondLead.LeadId);
            Assert.Contains(mine.Items, lead => lead.LeadId == firstLead.LeadId);
        }
    }

    // ---- What the lead view may reveal ---------------------------------------

    [Fact]
    public async Task A_new_lead_reports_its_lines_deadline_and_notes_and_never_the_buyers_private_data()
    {
        var (supplier, _, _) = await SignInAsSupplierAsync();
        using (supplier)
        {
            var (buyer, buyerToken) = await SignInAsBuyerAsync();
            RfqResponse rfq;
            using (buyer)
            {
                rfq = await SubmitRfqAsync(buyer, buyerToken, "Marine plywood for a warehouse fit-out");
            }

            SupplierLeadPayload row = await SingleLeadAsync(supplier, rfq.ReferenceCode);
            Assert.Equal("Matched", row.Status);
            Assert.Equal("Marine plywood for a warehouse fit-out", row.Title);
            Assert.Equal(2, row.LineCount);
            Assert.NotNull(row.ReceivedAt);
            Assert.NotNull(row.RespondBy);
            Assert.Null(row.ViewedAt);
            Assert.Null(row.DeclineReason);
            Assert.True(
                DateTimeOffset.Parse(row.RespondBy!, CultureInfo.InvariantCulture) > DateTimeOffset.UtcNow,
                "the respond-by date must be a real future instant, not a bare local timestamp");

            using HttpResponseMessage detail = await supplier.GetAsync($"{LeadsBase}/{row.LeadId}");
            Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
            string json = await detail.Content.ReadAsStringAsync();

            var lead = JsonSerializer.Deserialize<SupplierLeadDetailPayload>(json, Wire)!;
            Assert.Equal(row.LeadId, lead.LeadId);
            Assert.Equal("Matched", lead.Status);
            Assert.Equal(rfq.Id, lead.RfqId);
            Assert.Equal("Colombo 03, Sri Lanka", lead.DeliveryLocationText);
            Assert.Equal("Deliver within two weeks; BWP grade only.", lead.BuyerNotes);
            Assert.False(lead.SubstitutionsAllowed);
            Assert.Null(lead.NotifiedAt); // no notification was dispatched — the column keeps meaning "we reached them"
            Assert.Null(lead.ViewedAt);
            Assert.Null(lead.RespondedAt);
            Assert.Null(lead.DeclineReason);

            Assert.Equal(2, lead.Lines.Count);
            SupplierLeadLinePayload first = lead.Lines[0];
            Assert.Equal(1, first.LineNumber);
            Assert.Equal("Marine plywood 12mm sheets, 8x4", first.Description);
            Assert.Equal(25m, first.Quantity);
            Assert.Equal("each", first.UnitCode);
            Assert.Equal("BWP grade, moisture resistant", first.Specification);
            Assert.Equal(500m, lead.Lines[1].Quantity);

            // §6 "not shown": the buyer's budget (never revealed here), the
            // competing suppliers, their quotes, the match scoring and the
            // buyer's contact details are absent from the wire shape entirely —
            // this is a leak test, not a formatting test.
            foreach (string forbidden in new[]
            {
                "budget", "matchScore", "matchReasons", "organizationId", "organizationName",
                "specialInstructions", "email", "phone", "contactPerson",
            })
            {
                Assert.DoesNotContain(forbidden, json, StringComparison.OrdinalIgnoreCase);
            }

            Assert.Contains("buyerNotes", json, StringComparison.Ordinal);
        }
    }

    // ---- Opening a lead -------------------------------------------------------

    [Fact]
    public async Task Opening_a_lead_is_recorded_once_and_moves_it_from_new_to_follow_up()
    {
        var (supplier, token, _) = await SignInAsSupplierAsync();
        using (supplier)
        {
            var (buyer, buyerToken) = await SignInAsBuyerAsync();
            RfqResponse rfq;
            using (buyer)
            {
                rfq = await SubmitRfqAsync(buyer, buyerToken, "Cable tray for a plant");
            }

            SupplierLeadPayload row = await SingleLeadAsync(supplier, rfq.ReferenceCode);
            SupplierLeadPagePayload news = await ListAsync(supplier, "?status=new");
            Assert.Contains(news.Items, lead => lead.LeadId == row.LeadId);

            SupplierLeadDetailPayload first;
            using (HttpResponseMessage opened = await PostAsync(supplier, $"{LeadsBase}/{row.LeadId}/view", new { }, token))
            {
                Assert.Equal(HttpStatusCode.OK, opened.StatusCode);
                first = (await opened.Content.ReadFromJsonAsync<SupplierLeadDetailPayload>(Wire))!;
            }

            Assert.Equal("Viewed", first.Status);
            Assert.NotNull(first.ViewedAt);

            // Idempotent: reopening keeps the FIRST open, which is what §67 H
            // response health measures.
            using (HttpResponseMessage again = await PostAsync(supplier, $"{LeadsBase}/{row.LeadId}/view", new { }, token))
            {
                Assert.Equal(HttpStatusCode.OK, again.StatusCode);
                var second = (await again.Content.ReadFromJsonAsync<SupplierLeadDetailPayload>(Wire))!;
                Assert.Equal("Viewed", second.Status);
                Assert.Equal(first.ViewedAt, second.ViewedAt);
            }

            Assert.DoesNotContain(
                (await ListAsync(supplier, "?status=new")).Items, lead => lead.LeadId == row.LeadId);
            Assert.Contains(
                (await ListAsync(supplier, "?status=follow_up")).Items, lead => lead.LeadId == row.LeadId);
        }
    }

    // ---- Declining a lead -----------------------------------------------------

    [Fact]
    public async Task Declining_needs_a_reason_and_moves_the_lead_to_lost()
    {
        var (supplier, token, _) = await SignInAsSupplierAsync();
        using (supplier)
        {
            var (buyer, buyerToken) = await SignInAsBuyerAsync();
            RfqResponse rfq;
            using (buyer)
            {
                rfq = await SubmitRfqAsync(buyer, buyerToken, "Sub-panel for a garment factory");
            }

            SupplierLeadPayload row = await SingleLeadAsync(supplier, rfq.ReferenceCode);

            using (HttpResponseMessage blank = await PostAsync(
                supplier, $"{LeadsBase}/{row.LeadId}/decline", new { reason = "   " }, token))
            {
                Assert.Equal(HttpStatusCode.UnprocessableEntity, blank.StatusCode);
                var envelope = await blank.Content.ReadFromJsonAsync<ErrorEnvelope>();
                Assert.Equal(ErrorCodes.ValidationFailed, envelope!.Code);
                Assert.Equal("reason", Assert.Single(envelope!.FieldErrors!).Field);
            }

            using (HttpResponseMessage tooLong = await PostAsync(
                supplier, $"{LeadsBase}/{row.LeadId}/decline", new { reason = new string('x', 401) }, token))
            {
                Assert.Equal(HttpStatusCode.UnprocessableEntity, tooLong.StatusCode);
                var envelope = await tooLong.Content.ReadFromJsonAsync<ErrorEnvelope>();
                Assert.Equal("reason", Assert.Single(envelope!.FieldErrors!).Field);
            }

            SupplierLeadDetailPayload declined;
            using (HttpResponseMessage response = await PostAsync(
                supplier, $"{LeadsBase}/{row.LeadId}/decline",
                new { reason = "We do not stock armoured cable in that size." }, token))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                declined = (await response.Content.ReadFromJsonAsync<SupplierLeadDetailPayload>(Wire))!;
            }

            Assert.Equal("Declined", declined.Status);
            Assert.Equal("We do not stock armoured cable in that size.", declined.DeclineReason);
            Assert.NotNull(declined.RespondedAt); // a decline is a response (§5.4 → §67 H)

            Assert.Contains(
                (await ListAsync(supplier, "?status=lost")).Items, lead => lead.LeadId == row.LeadId);
            Assert.DoesNotContain(
                (await ListAsync(supplier, "?status=new")).Items, lead => lead.LeadId == row.LeadId);
            Assert.DoesNotContain(
                (await ListAsync(supplier, "?status=follow_up")).Items, lead => lead.LeadId == row.LeadId);

            // A resolved lead stays readable (the inbox shows why it was lost).
            using (HttpResponseMessage read = await supplier.GetAsync($"{LeadsBase}/{row.LeadId}"))
            {
                Assert.Equal(HttpStatusCode.OK, read.StatusCode);
                var detail = (await read.Content.ReadFromJsonAsync<SupplierLeadDetailPayload>(Wire))!;
                Assert.Equal("Declined", detail.Status);
                Assert.Equal("We do not stock armoured cable in that size.", detail.DeclineReason);
                Assert.Equal(2, detail.Lines.Count);
            }

            // §11.2 declares "any → Declined": a second decline is allowed and
            // holds the newest reason, with the audit trail keeping both.
            using (HttpResponseMessage again = await PostAsync(
                supplier, $"{LeadsBase}/{row.LeadId}/decline", new { reason = "Correction: out of stock." }, token))
            {
                Assert.Equal(HttpStatusCode.OK, again.StatusCode);
                var second = (await again.Content.ReadFromJsonAsync<SupplierLeadDetailPayload>(Wire))!;
                Assert.Equal("Declined", second.Status);
                Assert.Equal("Correction: out of stock.", second.DeclineReason);
            }
        }
    }

    // ---- The feed -------------------------------------------------------------

    [Fact]
    public async Task The_feed_pages_by_cursor_without_gaps_or_repeats_even_on_a_tie()
    {
        var (supplier, _, supplierId) = await SignInAsSupplierAsync();
        using (supplier)
        {
            var (buyer, buyerToken) = await SignInAsBuyerAsync();
            using (buyer)
            {
                var references = new List<string>();
                for (int index = 0; index < 3; index++)
                {
                    RfqResponse rfq = await SubmitRfqAsync(buyer, buyerToken, $"Paging fixture {index + 1}");
                    references.Add(rfq.ReferenceCode);
                }

                SupplierLeadPagePayload all = await ListAsync(supplier, "?limit=100");
                Assert.Equal(3, all.Items.Count);
                Assert.False(all.HasMore);
                Assert.Null(all.NextCursor);

                // Force the tie the keyset must survive: two leads whose arrival
                // is the identical instant, so only the (sortValue, leadId) pair
                // can order them.
                await WithDbAsync(async db =>
                {
                    List<RfqMatch> matches = await db.RfqMatches
                        .Where(match => match.SupplierId == supplierId)
                        .OrderBy(match => match.PublicId)
                        .Take(2)
                        .ToListAsync();
                    Assert.Equal(2, matches.Count);

                    DateTime shared = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
                    foreach (RfqMatch match in matches)
                    {
                        Rfq rfq = await db.Rfqs.SingleAsync(candidate => candidate.PublicId == match.RfqId);
                        rfq.SubmittedAt = shared;
                    }

                    await db.SaveChangesAsync();
                });

                HashSet<string> seen = new(StringComparer.Ordinal);
                string? cursor = null;
                int pages = 0;
                do
                {
                    string query = cursor is null ? "?limit=1" : $"?limit=1&cursor={Uri.EscapeDataString(cursor)}";
                    SupplierLeadPagePayload page = await ListAsync(supplier, query);
                    Assert.NotEmpty(page.Items);
                    foreach (SupplierLeadPayload lead in page.Items)
                    {
                        Assert.True(seen.Add(lead.LeadId), $"lead {lead.LeadId} was handed out twice");
                    }

                    cursor = page.NextCursor;
                    pages++;
                    Assert.True(pages < 10, "the cursor never reached the end of a three-row feed");
                }
                while (cursor is not null);

                Assert.Equal(3, seen.Count);
                Assert.Equal(all.Items.Select(lead => lead.LeadId).ToHashSet(StringComparer.Ordinal), seen);
                Assert.All(references, reference =>
                    Assert.Contains(all.Items, lead => lead.ReferenceCode == reference));

                // A page size outside the contract and a forged cursor are client
                // mistakes, named on the field that carries them.
                using (HttpResponseMessage tooSmall = await supplier.GetAsync($"{LeadsBase}?limit=0"))
                {
                    Assert.Equal(HttpStatusCode.UnprocessableEntity, tooSmall.StatusCode);
                    var envelope = await tooSmall.Content.ReadFromJsonAsync<ErrorEnvelope>();
                    Assert.Equal("limit", Assert.Single(envelope!.FieldErrors!).Field);
                }

                using (HttpResponseMessage tooBig = await supplier.GetAsync($"{LeadsBase}?limit=101"))
                {
                    Assert.Equal(HttpStatusCode.UnprocessableEntity, tooBig.StatusCode);
                }

                using (HttpResponseMessage forged = await supplier.GetAsync($"{LeadsBase}?cursor=not-a-cursor"))
                {
                    Assert.Equal(HttpStatusCode.UnprocessableEntity, forged.StatusCode);
                    var envelope = await forged.Content.ReadFromJsonAsync<ErrorEnvelope>();
                    Assert.Equal("cursor", Assert.Single(envelope!.FieldErrors!).Field);
                }
            }
        }
    }

    [Fact]
    public async Task Unknown_views_are_422_and_views_that_need_a_quote_answer_empty_rather_than_lie()
    {
        var (supplier, _, _) = await SignInAsSupplierAsync();
        using (supplier)
        {
            var (buyer, buyerToken) = await SignInAsBuyerAsync();
            RfqResponse rfq;
            using (buyer)
            {
                rfq = await SubmitRfqAsync(buyer, buyerToken, "Conduit and trunking");
            }

            SupplierLeadPayload row = await SingleLeadAsync(supplier, rfq.ReferenceCode);
            Guid leadId = Guid.Parse(row.LeadId);

            using (HttpResponseMessage unknown = await supplier.GetAsync($"{LeadsBase}?status=not_a_view"))
            {
                Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.StatusCode);
                var envelope = await unknown.Content.ReadFromJsonAsync<ErrorEnvelope>();
                Assert.Equal(ErrorCodes.ValidationFailed, envelope!.Code);
                Assert.Equal("status", Assert.Single(envelope!.FieldErrors!).Field);
            }

            // `won` is the supplier's quote ACCEPTED (§11.3). Acceptance is the
            // buyer's act and no quote-acceptance use case ships in this phase, so
            // nothing can be won: the view answers empty rather than infer a win
            // from the RFQ's own Awarded status, which cannot say which supplier
            // won. `quoted` is empty here only because this test never submits a
            // quote — a submitted quote moves the lead to `Quoting` (§2.7).
            Assert.Empty((await ListAsync(supplier, "?status=won")).Items);
            Assert.Empty((await ListAsync(supplier, "?status=quoted")).Items);

            // `due_soon` is driven by the respond-by date: 21 days out it is not
            // due, two hours out it is, and it stays `new` throughout.
            Assert.Empty((await ListAsync(supplier, "?status=due_soon")).Items);

            await WithDbAsync(async db =>
            {
                RfqMatch match = await db.RfqMatches.SingleAsync(candidate => candidate.PublicId == leadId);
                match.ExpiryAt = DateTime.UtcNow.AddHours(2);
                await db.SaveChangesAsync();
            });

            Assert.Contains(
                (await ListAsync(supplier, "?status=due_soon")).Items, lead => lead.LeadId == row.LeadId);
            Assert.Contains(
                (await ListAsync(supplier, "?status=new")).Items, lead => lead.LeadId == row.LeadId);

            await WithDbAsync(async db =>
            {
                RfqMatch match = await db.RfqMatches.SingleAsync(candidate => candidate.PublicId == leadId);
                match.ExpiryAt = DateTime.UtcNow.AddDays(10);
                await db.SaveChangesAsync();
            });

            Assert.DoesNotContain(
                (await ListAsync(supplier, "?status=due_soon")).Items, lead => lead.LeadId == row.LeadId);
        }
    }

    // ---- Helpers --------------------------------------------------------------

    /// <summary>
    /// Registers a supplier covering <see cref="Department"/>, signs it in, and
    /// resolves its supplier id — the id the matching pipeline keys its lead rows
    /// on, which the registration response does not echo.
    /// </summary>
    private async Task<(HttpClient Client, string Token, Guid SupplierId)> SignInAsSupplierAsync()
    {
        HttpClient client = AntiforgeryTestSupport.HttpsClient(_factory);
        string email = NewEmail("supplier");
        string company = $"Lead Fixture {Guid.NewGuid():N} (Pvt) Ltd";

        string token = await AntiforgeryTestSupport.AcquireTokenAsync(client);
        using (HttpResponseMessage registered = await PostAsync(
            client,
            $"{ApiBase}/auth/register/supplier",
            new
            {
                email,
                password = Password,
                contactFirstName = "Nimal",
                contactLastName = "Perera",
                phone = "+94771234567",
                companyName = company,
                registrationNumber = "PV-77777",
                website = "https://lead-fixture.example",
                categories = new[] { Department },
            },
            token))
        {
            Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        }

        Guid supplierId;
        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StockpileDbContext>();
            supplierId = await db.Suppliers
                .Where(supplier => supplier.DisplayName == company)
                .Select(supplier => supplier.PublicId)
                .SingleAsync();
        }

        token = await AntiforgeryTestSupport.AcquireTokenAsync(client);
        using (HttpResponseMessage login = await PostAsync(
            client, $"{ApiBase}/auth/login", new { email, password = Password }, token))
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
        using (HttpResponseMessage registered = await PostAsync(
            client, $"{ApiBase}/auth/register", new { email, password = Password }, token))
        {
            Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        }

        token = await AntiforgeryTestSupport.AcquireTokenAsync(client);
        using (HttpResponseMessage login = await PostAsync(
            client, $"{ApiBase}/auth/login", new { email, password = Password }, token))
        {
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        }

        return (client, await AntiforgeryTestSupport.AcquireTokenAsync(client));
    }

    /// <summary>Creates and submits a two-line RFQ in <see cref="Department"/> and returns the submitted body.</summary>
    private static async Task<RfqResponse> SubmitRfqAsync(HttpClient buyer, string token, string title)
    {
        var payload = new CreateRfqPayload(
            title,
            new CompanyPayload("Buildwell Projects (Pvt) Ltd", "PV-12345", "https://buildwell.example"),
            "Colombo 03, Sri Lanka",
            SubstitutionsAllowed: false,
            SpecialInstructions: "Deliver within two weeks; BWP grade only.",
            ExpiryDays: 21,
            [
                new ItemPayload(
                    "Marine plywood 12mm sheets, 8x4", 25, Department, "each", "BWP grade, moisture resistant"),
                new ItemPayload("Countersunk screws 50mm", 500, Department, "each", null),
            ]);

        using HttpResponseMessage created = await PostAsync(buyer, $"{ApiBase}/rfqs", payload, token);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var draft = await created.Content.ReadFromJsonAsync<RfqResponse>(Wire);
        Assert.Equal("Draft", draft!.Status);

        string submitToken = await AntiforgeryTestSupport.AcquireTokenAsync(buyer);
        using HttpResponseMessage submitted = await PostAsync(
            buyer, $"{ApiBase}/rfqs/{draft.Id}/submit", new { }, submitToken);
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
        return (await submitted.Content.ReadFromJsonAsync<RfqResponse>(Wire))!;
    }

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client, string route, object payload, string requestToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, route)
        {
            Content = JsonContent.Create(payload, options: Wire),
        };

        // Critical writes (RFQ submit, quote/order actions) require a fresh key per call;
        // every other route ignores the header.
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return await AntiforgeryTestSupport.SendUnsafeAsync(client, request, requestToken);
    }

    private static async Task<SupplierLeadPagePayload> ListAsync(HttpClient client, string query = "")
    {
        using HttpResponseMessage response = await client.GetAsync($"{LeadsBase}{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SupplierLeadPagePayload>(Wire))!;
    }

    /// <summary>The one lead this supplier holds for the RFQ (its inbox starts empty — matching is per supplier).</summary>
    private static async Task<SupplierLeadPayload> SingleLeadAsync(HttpClient client, string referenceCode)
    {
        SupplierLeadPagePayload page = await ListAsync(client, "?limit=100");
        return Assert.Single(page.Items, lead => lead.ReferenceCode == referenceCode);
    }

    private async Task WithDbAsync(Func<StockpileDbContext, Task> work)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StockpileDbContext>();
        await work(db);
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

    private sealed record SupplierLeadPagePayload(
        List<SupplierLeadPayload> Items, string? NextCursor, bool HasMore);

    private sealed record SupplierLeadPayload(
        string LeadId,
        string Status,
        string ReferenceCode,
        string Title,
        string? ReceivedAt,
        string? RespondBy,
        string? RequestedDeliveryAt,
        int LineCount,
        string? ViewedAt,
        string? DeclineReason);

    private sealed record SupplierLeadDetailPayload(
        string LeadId,
        string Status,
        string RfqId,
        string ReferenceCode,
        string Title,
        string? DeliveryLocationText,
        string? RequestedDeliveryAt,
        string? RespondBy,
        bool SubstitutionsAllowed,
        string? BuyerNotes,
        List<SupplierLeadLinePayload> Lines,
        string? NotifiedAt,
        string? ViewedAt,
        string? RespondedAt,
        string? DeclineReason);

    private sealed record SupplierLeadLinePayload(
        string RfqItemId,
        int LineNumber,
        string Description,
        string? Specification,
        decimal Quantity,
        string? UnitCode,
        string? ExpectedAt,
        string? Notes);
}
