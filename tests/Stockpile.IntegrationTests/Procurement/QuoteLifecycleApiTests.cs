using System.Net;
using Stockpile.Contracts.Errors;
using Stockpile.Contracts.Procurement;
using Stockpile.IntegrationTests.Testing;
using static Stockpile.IntegrationTests.Testing.MarketplaceTestKit;

namespace Stockpile.IntegrationTests.Procurement;

/// <summary>
/// The quote lifecycle after submission over real HTTP (api.md §2.7,
/// business-flows §8–§9): reads from either side, the buyer's revision request and
/// the supplier's append-only revisions, withdraw, decline, and the comparison view.
/// <para>
/// What this pins: every quote read and write is scoped to its own two parties —
/// a stranger (another supplier or another buyer) gets the one 404, never a 403 or
/// a hint the quote exists (BOLA, api.md §1.6); a revision never edits an earlier
/// one; each side can do only what its own capability allows; and the comparison
/// says "nothing to compare" instead of inventing a ranking.
/// </para>
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class QuoteLifecycleApiTests(StockpileApiFactory factory)
{
    private readonly MarketplaceTestKit _kit = new(factory);

    [Fact]
    public async Task Both_parties_read_the_quote_and_a_stranger_gets_one_404()
    {
        Scenario scenario = await _kit.CreateQuoteAsync();
        string route = $"{ApiBase}/quotes/{scenario.Quote.PublicId}";

        ApiResult asSupplier = await GetAsync(scenario.Supplier, route);
        Assert.Equal(HttpStatusCode.OK, asSupplier.Status);
        Assert.Equal("supplier", asSupplier.As<QuoteDetailResponse>().Viewer);

        ApiResult asBuyer = await GetAsync(scenario.Buyer, route);
        Assert.Equal(HttpStatusCode.OK, asBuyer.Status);
        QuoteDetailResponse buyerView = asBuyer.As<QuoteDetailResponse>();
        Assert.Equal("buyer", buyerView.Viewer);
        Assert.Equal("Submitted", buyerView.Status);
        Assert.Single(buyerView.Revisions);
        Assert.Equal("Active", buyerView.CurrentRevision.Status);
        Assert.Equal(scenario.Quote.Totals.GrandTotal, buyerView.CurrentRevision.Totals.GrandTotal);

        // BOLA — a different supplier and a different buyer, each with a real
        // account and the right capability, still see nothing.
        Actor otherSupplier = await _kit.RegisterSupplierAsync();
        ApiResult supplierProbe = await GetAsync(otherSupplier, route);
        Assert.Equal(HttpStatusCode.NotFound, supplierProbe.Status);
        Assert.Equal(ErrorCodes.ResourceNotFound, supplierProbe.Code);

        Actor otherBuyer = await _kit.RegisterBuyerAsync();
        await SubmitRfqAsync(otherBuyer, "Another buyer's own RFQ");
        ApiResult buyerProbe = await GetAsync(otherBuyer, route);
        Assert.Equal(HttpStatusCode.NotFound, buyerProbe.Status);
        Assert.Equal(ErrorCodes.ResourceNotFound, buyerProbe.Code);

        // A missing id answers exactly like a foreign one.
        ApiResult missing = await GetAsync(otherSupplier, $"{ApiBase}/quotes/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, missing.Status);
        Assert.Equal(supplierProbe.Code, missing.Code);
    }

    [Fact]
    public async Task Lists_hold_only_the_callers_own_quotes()
    {
        Scenario scenario = await _kit.CreateQuoteAsync();
        Actor otherSupplier = await _kit.RegisterSupplierAsync();

        ApiResult mine = await GetAsync(scenario.Supplier, $"{ApiBase}/suppliers/me/quotes");
        Assert.Equal(HttpStatusCode.OK, mine.Status);
        Assert.Contains(mine.As<List<QuoteSummaryResponse>>(), row => row.PublicId == scenario.Quote.PublicId);

        ApiResult others = await GetAsync(otherSupplier, $"{ApiBase}/suppliers/me/quotes");
        Assert.Equal(HttpStatusCode.OK, others.Status);
        Assert.DoesNotContain(others.As<List<QuoteSummaryResponse>>(), row => row.PublicId == scenario.Quote.PublicId);

        ApiResult received = await GetAsync(scenario.Buyer, $"{ApiBase}/me/quotes");
        Assert.Equal(HttpStatusCode.OK, received.Status);
        QuoteSummaryResponse row = Assert.Single(received.As<List<QuoteSummaryResponse>>(), r => r.PublicId == scenario.Quote.PublicId);
        Assert.Equal(scenario.Reference, row.RfqReference);

        // Each list is gated by its own side's capability.
        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(scenario.Buyer, $"{ApiBase}/suppliers/me/quotes")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(scenario.Supplier, $"{ApiBase}/me/quotes")).Status);
    }

    [Fact]
    public async Task A_revision_is_appended_and_never_edits_the_one_before()
    {
        Scenario scenario = await _kit.CreateQuoteAsync(unitPrice: 100m);
        string quote = scenario.Quote.PublicId;

        // A supplier cannot revise on its own initiative: the buyer asks first.
        ApiResult early = await PostAsync(scenario.Supplier, $"{ApiBase}/quotes/{quote}/revisions", Revision(scenario.Lead, 90m));
        Assert.Equal(HttpStatusCode.Conflict, early.Status);
        Assert.Equal(ErrorCodes.StateTransitionInvalid, early.Code);

        ApiResult asked = await PostAsync(
            scenario.Buyer,
            $"{ApiBase}/quotes/{quote}/revision-requests",
            new RequestQuoteRevisionRequest("PriceRequest", "Can you do better on price for this volume?"));
        Assert.Equal(HttpStatusCode.OK, asked.Status);
        Assert.Equal("RevisionRequested", asked.As<QuoteSummaryResponse>().Status);

        // The supplier is not the buyer: it cannot ask itself.
        ApiResult selfAsk = await PostAsync(
            scenario.Supplier,
            $"{ApiBase}/quotes/{quote}/revision-requests",
            new RequestQuoteRevisionRequest("PriceRequest", "Supplier asking itself"));
        Assert.Equal(HttpStatusCode.Forbidden, selfAsk.Status);

        ApiResult revised = await PostAsync(
            scenario.Supplier, $"{ApiBase}/quotes/{quote}/revisions", Revision(scenario.Lead, 90m, supplierNote: "Volume discount applied"));
        Assert.Equal(HttpStatusCode.Created, revised.Status);
        QuoteResponse second = revised.As<QuoteResponse>();
        Assert.Equal(2, second.RevisionNumber);
        Assert.Equal("Revised", second.Status);
        Assert.True(second.Totals.GrandTotal < scenario.Quote.Totals.GrandTotal);

        QuoteDetailResponse detail = (await GetAsync(scenario.Buyer, $"{ApiBase}/quotes/{quote}")).As<QuoteDetailResponse>();
        Assert.Equal(2, detail.Revisions.Count);
        QuoteRevisionResponse first = detail.Revisions.Single(r => r.RevisionNumber == 1);
        Assert.Equal("Superseded", first.Status);
        Assert.Equal(scenario.Quote.Totals, first.Totals); // revision 1 is exactly as the buyer first saw it
        Assert.Equal("Active", detail.Revisions.Single(r => r.RevisionNumber == 2).Status);
        Assert.Equal(2, detail.RevisionNumber);

        // The thread keeps both sides' words, tied to the revision they were about.
        Assert.Contains(detail.Negotiation, m => m.Direction == "BuyerToSupplier" && m.AgainstRevisionNumber == 1);
        Assert.Contains(detail.Negotiation, m => m.Direction == "SupplierToBuyer" && m.Body.Contains("Volume discount", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_revision_replays_under_the_same_idempotency_key_without_a_second_revision()
    {
        Scenario scenario = await _kit.CreateQuoteAsync();
        string quote = scenario.Quote.PublicId;
        await PostAsync(
            scenario.Buyer,
            $"{ApiBase}/quotes/{quote}/revision-requests",
            new RequestQuoteRevisionRequest("LeadTimeRequest", "We need delivery sooner than a week."));

        string key = Guid.NewGuid().ToString();
        ApiResult first = await PostAsync(scenario.Supplier, $"{ApiBase}/quotes/{quote}/revisions", Revision(scenario.Lead, 70m), key);
        Assert.Equal(HttpStatusCode.Created, first.Status);
        ApiResult replay = await PostAsync(scenario.Supplier, $"{ApiBase}/quotes/{quote}/revisions", Revision(scenario.Lead, 70m), key);
        Assert.True(replay.Replayed);

        QuoteDetailResponse detail = (await GetAsync(scenario.Supplier, $"{ApiBase}/quotes/{quote}")).As<QuoteDetailResponse>();
        Assert.Equal(2, detail.Revisions.Count);
    }

    [Fact]
    public async Task Only_the_owning_supplier_can_withdraw_and_a_withdrawn_quote_is_final()
    {
        Scenario scenario = await _kit.CreateQuoteAsync();
        string quote = scenario.Quote.PublicId;
        Actor otherSupplier = await _kit.RegisterSupplierAsync();

        ApiResult foreign = await PostAsync(otherSupplier, $"{ApiBase}/quotes/{quote}/withdraw");
        Assert.Equal(HttpStatusCode.NotFound, foreign.Status);

        ApiResult withdrawn = await PostAsync(scenario.Supplier, $"{ApiBase}/quotes/{quote}/withdraw");
        Assert.Equal(HttpStatusCode.OK, withdrawn.Status);
        Assert.Equal("Withdrawn", withdrawn.As<QuoteSummaryResponse>().Status);

        // The buyer cannot accept what was withdrawn, and the supplier cannot withdraw twice.
        ApiResult accept = await PostAsync(scenario.Buyer, $"{ApiBase}/quotes/{quote}/accept");
        Assert.Equal(HttpStatusCode.Conflict, accept.Status);
        Assert.Equal(ErrorCodes.QuoteImmutable, accept.Code);

        ApiResult again = await PostAsync(scenario.Supplier, $"{ApiBase}/quotes/{quote}/withdraw");
        Assert.Equal(HttpStatusCode.Conflict, again.Status);
    }

    [Fact]
    public async Task Decline_needs_a_reason_the_buyers_capability_and_records_the_reason()
    {
        Scenario scenario = await _kit.CreateQuoteAsync();
        string quote = scenario.Quote.PublicId;

        ApiResult supplierTries = await PostAsync(scenario.Supplier, $"{ApiBase}/quotes/{quote}/decline", new DeclineQuoteRequest("No"));
        Assert.Equal(HttpStatusCode.Forbidden, supplierTries.Status);
        Assert.Equal(ErrorCodes.InsufficientCapability, supplierTries.Code);

        ApiResult noReason = await PostAsync(scenario.Buyer, $"{ApiBase}/quotes/{quote}/decline", new DeclineQuoteRequest(string.Empty));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noReason.Status);

        ApiResult declined = await PostAsync(
            scenario.Buyer, $"{ApiBase}/quotes/{quote}/decline", new DeclineQuoteRequest("Lead time does not suit our programme."));
        Assert.Equal(HttpStatusCode.OK, declined.Status);

        QuoteDetailResponse detail = (await GetAsync(scenario.Supplier, $"{ApiBase}/quotes/{quote}")).As<QuoteDetailResponse>();
        Assert.Equal("Rejected", detail.Status);
        Assert.Equal("Lead time does not suit our programme.", detail.DeclineReason);
    }

    [Fact]
    public async Task Comparison_is_honestly_empty_below_two_quotes_and_then_factual_without_ranking()
    {
        // Matching happens when the RFQ is submitted, so both suppliers exist first.
        Actor first = await _kit.RegisterSupplierAsync();
        Actor second = await _kit.RegisterSupplierAsync();
        Actor buyer = await _kit.RegisterBuyerAsync();
        (string rfqId, string reference) = await SubmitRfqAsync(buyer, "Cables compared");

        // No quotes yet: an honest zero, not an error and not a placeholder.
        ApiResult none = await GetAsync(buyer, $"{ApiBase}/rfqs/{rfqId}/comparison");
        Assert.Equal(HttpStatusCode.OK, none.Status);
        RfqComparisonResponse empty = none.As<RfqComparisonResponse>();
        Assert.False(empty.Comparable);
        Assert.Equal(0, empty.QuoteCount);

        Lead firstLead = await FindLeadAsync(first, reference);
        Assert.Equal(HttpStatusCode.Created, (await PostAsync(first, $"{ApiBase}/quotes", FullQuote(firstLead, 80m))).Status);

        RfqComparisonResponse one = (await GetAsync(buyer, $"{ApiBase}/rfqs/{rfqId}/comparison")).As<RfqComparisonResponse>();
        Assert.False(one.Comparable);
        Assert.Equal(1, one.QuoteCount);
        Assert.Empty(one.Annotations);

        // A partial second quote makes the comparison real, and its gap is stated as a fact.
        Lead secondLead = await FindLeadAsync(second, reference);
        CreateQuoteRequest partial = FullQuote(secondLead, 95m) with
        {
            Coverage = "Partial",
            Items = [.. FullQuote(secondLead, 95m).Items.Take(1)],
        };
        Assert.Equal(HttpStatusCode.Created, (await PostAsync(second, $"{ApiBase}/quotes", partial)).Status);

        ApiResult both = await GetAsync(buyer, $"{ApiBase}/rfqs/{rfqId}/comparison");
        RfqComparisonResponse comparison = both.As<RfqComparisonResponse>();
        Assert.True(comparison.Comparable);
        Assert.Equal(2, comparison.Quotes.Count);
        Assert.Contains(comparison.Annotations, a => a.Kind == "coverage_gap");
        Assert.DoesNotContain("rank", both.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("best", both.Body, StringComparison.OrdinalIgnoreCase);

        // Another buyer cannot read this RFQ's comparison; a supplier lacks the capability altogether.
        Actor other = await _kit.RegisterBuyerAsync();
        await SubmitRfqAsync(other, "Unrelated RFQ for the second buyer");
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(other, $"{ApiBase}/rfqs/{rfqId}/comparison")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(first, $"{ApiBase}/rfqs/{rfqId}/comparison")).Status);
    }
}
