using System.Net;
using Microsoft.EntityFrameworkCore;
using Stockpile.Contracts.Errors;
using Stockpile.Contracts.Orders;
using Stockpile.Contracts.Procurement;
using Stockpile.Domain.Entities.Catalogue;
using Stockpile.IntegrationTests.Testing;
using static Stockpile.IntegrationTests.Testing.MarketplaceTestKit;

namespace Stockpile.IntegrationTests.Orders;

/// <summary>
/// Quote acceptance and the order surface over real HTTP (api.md §2.7/§2.8,
/// business-flows §10).
/// <para>
/// What this pins: accepting a quote creates its order in ONE transaction (quote
/// Accepted, RFQ Ordered, order + lines + status ledger together) with the accepted
/// revision's own figures; a retry of the same <c>Idempotency-Key</c> replays and a
/// DIFFERENT key — including a burst of concurrent accepts — still yields exactly
/// one order; an elapsed validity is <c>QUOTE_EXPIRED</c> and creates nothing; stock
/// is held only for the quoting supplier's live-tracked products and a shortfall
/// refuses the acceptance with nothing changed; and both order reads are scoped to
/// the order's own two parties (BOLA).
/// </para>
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class OrderApiTests(StockpileApiFactory factory)
{
    private readonly MarketplaceTestKit _kit = new(factory);

    [Fact]
    public async Task Accepting_a_quote_creates_the_order_and_walks_quote_and_rfq_together()
    {
        Scenario scenario = await _kit.CreateQuoteAsync(unitPrice: 120m);

        ApiResult accepted = await PostAsync(scenario.Buyer, $"{ApiBase}/quotes/{scenario.Quote.PublicId}/accept");
        Assert.Equal(HttpStatusCode.Created, accepted.Status);
        AcceptQuoteResponse created = accepted.As<AcceptQuoteResponse>();
        Assert.StartsWith("PO-", created.OrderNumber, StringComparison.Ordinal);
        Assert.Equal("Confirmed", created.Status);
        Assert.Equal("NotRequired", created.StockReservationState); // no tracked stock was quoted

        // The quote and the RFQ moved in the same pass.
        QuoteDetailResponse quote = (await GetAsync(scenario.Buyer, $"{ApiBase}/quotes/{scenario.Quote.PublicId}")).As<QuoteDetailResponse>();
        Assert.Equal("Accepted", quote.Status);
        Assert.Equal(created.OrderId, quote.OrderId);
        ApiResult rfq = await GetAsync(scenario.Buyer, $"{ApiBase}/rfqs/{scenario.RfqId}");
        Assert.Equal("Ordered", rfq.Str("status"));

        // The order carries the accepted revision's figures, line for line.
        OrderDetailResponse order = (await GetAsync(scenario.Buyer, $"{ApiBase}/orders/{created.OrderId}")).As<OrderDetailResponse>();
        Assert.Equal(scenario.Quote.Totals.GrandTotal, order.GrandTotal);
        Assert.Equal(scenario.Quote.Totals.Subtotal, order.Subtotal);
        Assert.Equal("LKR", order.Currency);
        Assert.Equal(2, order.Items.Count);
        Assert.Equal(new[] { 1, 2 }, order.Items.Select(item => item.LineNumber).Order());
        Assert.All(order.Items, item => Assert.Equal(120m, item.UnitPrice));
        Assert.Contains(order.History, entry => entry.To == "Confirmed");

        // The supplier sees the same order from its own side.
        ApiResult asSupplier = await GetAsync(scenario.Supplier, $"{ApiBase}/orders/{created.OrderId}");
        Assert.Equal(HttpStatusCode.OK, asSupplier.Status);
        Assert.Equal(created.OrderNumber, asSupplier.As<OrderDetailResponse>().OrderNumber);

        Assert.Contains(
            (await GetAsync(scenario.Buyer, $"{ApiBase}/me/orders")).As<List<OrderSummaryResponse>>(),
            row => row.PublicId == created.OrderId);
        Assert.Contains(
            (await GetAsync(scenario.Supplier, $"{ApiBase}/suppliers/me/orders")).As<List<OrderSummaryResponse>>(),
            row => row.PublicId == created.OrderId);

        // One order row, one accepted quote — checked in the database, not just through the API.
        int orders = await _kit.WithDbAsync(db => db.PurchaseOrders.CountAsync(o => o.QuoteId == Guid.Parse(scenario.Quote.PublicId)));
        Assert.Equal(1, orders);
    }

    [Fact]
    public async Task The_same_idempotency_key_replays_the_original_order()
    {
        Scenario scenario = await _kit.CreateQuoteAsync();
        string route = $"{ApiBase}/quotes/{scenario.Quote.PublicId}/accept";
        string key = Guid.NewGuid().ToString();

        ApiResult first = await PostAsync(scenario.Buyer, route, idempotencyKey: key);
        Assert.Equal(HttpStatusCode.Created, first.Status);
        ApiResult replay = await PostAsync(scenario.Buyer, route, idempotencyKey: key);

        Assert.True(replay.Replayed);
        Assert.Equal(first.Str("orderId"), replay.Str("reference"));
        int orders = await _kit.WithDbAsync(db => db.PurchaseOrders.CountAsync(o => o.QuoteId == Guid.Parse(scenario.Quote.PublicId)));
        Assert.Equal(1, orders);

        // A second, differently-keyed accept is not a replay: the quote is already accepted.
        ApiResult second = await PostAsync(scenario.Buyer, route);
        Assert.Equal(HttpStatusCode.Conflict, second.Status);
        Assert.Equal(ErrorCodes.QuoteAlreadyAccepted, second.Code);
    }

    [Fact]
    public async Task Concurrent_accepts_with_different_keys_create_exactly_one_order()
    {
        Scenario scenario = await _kit.CreateQuoteAsync();
        string route = $"{ApiBase}/quotes/{scenario.Quote.PublicId}/accept";

        ApiResult[] results = await Task.WhenAll(
            Enumerable.Range(0, 6).Select(_ => Task.Run(() => PostAsync(scenario.Buyer, route))));

        Assert.Single(results, result => result.Status == HttpStatusCode.Created);
        foreach (ApiResult loser in results.Where(result => result.Status != HttpStatusCode.Created))
        {
            Assert.Equal(HttpStatusCode.Conflict, loser.Status);
            Assert.Equal(ErrorCodes.QuoteAlreadyAccepted, loser.Code);
        }

        int orders = await _kit.WithDbAsync(db => db.PurchaseOrders.CountAsync(o => o.QuoteId == Guid.Parse(scenario.Quote.PublicId)));
        Assert.Equal(1, orders);
    }

    [Fact]
    public async Task An_expired_quote_is_refused_and_creates_nothing()
    {
        Scenario scenario = await _kit.CreateQuoteAsync();
        Guid quoteId = Guid.Parse(scenario.Quote.PublicId);
        await _kit.WithDbAsync(async db =>
        {
            Stockpile.Domain.Entities.Procurement.Quote row = await db.Quotes.SingleAsync(q => q.PublicId == quoteId);
            row.ValidUntil = DateTime.UtcNow.AddMinutes(-5);
            await db.SaveChangesAsync();
        });

        ApiResult accepted = await PostAsync(scenario.Buyer, $"{ApiBase}/quotes/{quoteId}/accept");
        Assert.Equal(HttpStatusCode.Forbidden, accepted.Status); // QUOTE_EXPIRED is registered as 403
        Assert.Equal(ErrorCodes.QuoteExpired, accepted.Code);

        Assert.Equal(0, await _kit.WithDbAsync(db => db.PurchaseOrders.CountAsync(o => o.QuoteId == quoteId)));
        QuoteDetailResponse read = (await GetAsync(scenario.Buyer, $"{ApiBase}/quotes/{quoteId}")).As<QuoteDetailResponse>();
        Assert.Equal("Expired", read.Status); // expiry is derived on read
        ApiResult rfq = await GetAsync(scenario.Buyer, $"{ApiBase}/rfqs/{scenario.RfqId}");
        Assert.NotEqual("Ordered", rfq.Str("status"));
    }

    [Fact]
    public async Task Only_the_owning_buyer_with_the_accept_capability_can_accept()
    {
        Scenario scenario = await _kit.CreateQuoteAsync();
        string route = $"{ApiBase}/quotes/{scenario.Quote.PublicId}/accept";

        // A supplier holds no quote.accept.
        ApiResult asSupplier = await PostAsync(scenario.Supplier, route);
        Assert.Equal(HttpStatusCode.Forbidden, asSupplier.Status);

        // Another buyer (own company, own RFQ) has the capability but not the quote.
        Actor stranger = await _kit.RegisterBuyerAsync();
        await SubmitRfqAsync(stranger, "The stranger's own RFQ");
        ApiResult asStranger = await PostAsync(stranger, route);
        Assert.Equal(HttpStatusCode.NotFound, asStranger.Status);
        Assert.Equal(ErrorCodes.ResourceNotFound, asStranger.Code);

        Assert.Equal(0, await _kit.WithDbAsync(db => db.PurchaseOrders.CountAsync(o => o.QuoteId == Guid.Parse(scenario.Quote.PublicId))));
    }

    [Fact]
    public async Task Order_reads_are_scoped_to_the_two_parties()
    {
        Scenario scenario = await _kit.CreateQuoteAsync();
        AcceptQuoteResponse created = (await PostAsync(scenario.Buyer, $"{ApiBase}/quotes/{scenario.Quote.PublicId}/accept")).As<AcceptQuoteResponse>();

        Actor otherSupplier = await _kit.RegisterSupplierAsync();
        Actor otherBuyer = await _kit.RegisterBuyerAsync();
        await SubmitRfqAsync(otherBuyer, "Some other buyer's RFQ");

        foreach (Actor stranger in new[] { otherSupplier, otherBuyer })
        {
            ApiResult probe = await GetAsync(stranger, $"{ApiBase}/orders/{created.OrderId}");
            Assert.Equal(HttpStatusCode.NotFound, probe.Status);
            Assert.Equal(ErrorCodes.ResourceNotFound, probe.Code);
        }

        Assert.DoesNotContain(
            (await GetAsync(otherSupplier, $"{ApiBase}/suppliers/me/orders")).As<List<OrderSummaryResponse>>(),
            row => row.PublicId == created.OrderId);
        Assert.DoesNotContain(
            (await GetAsync(otherBuyer, $"{ApiBase}/me/orders")).As<List<OrderSummaryResponse>>(),
            row => row.PublicId == created.OrderId);
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(
            new Actor(AntiforgeryTestSupport.HttpsClient(factory), "anonymous", null), $"{ApiBase}/orders/{created.OrderId}")).Status);
    }

    [Fact]
    public async Task The_supplier_acknowledges_a_confirmed_order_once()
    {
        Scenario scenario = await _kit.CreateQuoteAsync();
        AcceptQuoteResponse created = (await PostAsync(scenario.Buyer, $"{ApiBase}/quotes/{scenario.Quote.PublicId}/accept")).As<AcceptQuoteResponse>();
        string route = $"{ApiBase}/orders/{created.OrderId}/acknowledge";

        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(scenario.Buyer, route)).Status);

        ApiResult acknowledged = await PostAsync(scenario.Supplier, route);
        Assert.Equal(HttpStatusCode.OK, acknowledged.Status);
        OrderDetailResponse order = acknowledged.As<OrderDetailResponse>();
        Assert.Equal("SupplierAcknowledged", order.Status);
        Assert.NotNull(order.AcknowledgedAt);

        // A second acknowledgement is a state conflict, not a silent second ledger row.
        ApiResult again = await PostAsync(scenario.Supplier, route);
        Assert.Equal(HttpStatusCode.Conflict, again.Status);
        Assert.Equal(ErrorCodes.StateTransitionInvalid, again.Code);
    }

    [Fact]
    public async Task Tracked_stock_of_the_quoting_supplier_is_held_on_accept()
    {
        Actor supplier = await _kit.RegisterSupplierAsync();
        Guid productId = await AddTrackedProductAsync(supplier.SupplierId!.Value, onHand: 25);
        Scenario scenario = await _kit.CreateQuoteAsync(supplier: supplier, productId: productId);

        ApiResult accepted = await PostAsync(scenario.Buyer, $"{ApiBase}/quotes/{scenario.Quote.PublicId}/accept");
        Assert.Equal(HttpStatusCode.Created, accepted.Status);
        Assert.Equal("Held", accepted.As<AcceptQuoteResponse>().StockReservationState);

        ProductStock stock = await _kit.WithDbAsync(db => db.ProductStocks.AsNoTracking().SingleAsync(s => s.ProductId == productId));
        Assert.Equal(10, stock.Reserved); // line 1 of the RFQ asks for 10
        Assert.Equal(25, stock.OnHand);
    }

    [Fact]
    public async Task A_stock_shortfall_refuses_the_accept_and_changes_nothing()
    {
        Actor supplier = await _kit.RegisterSupplierAsync();
        Guid productId = await AddTrackedProductAsync(supplier.SupplierId!.Value, onHand: 4);
        Scenario scenario = await _kit.CreateQuoteAsync(supplier: supplier, productId: productId);
        Guid quoteId = Guid.Parse(scenario.Quote.PublicId);

        ApiResult accepted = await PostAsync(scenario.Buyer, $"{ApiBase}/quotes/{quoteId}/accept");
        Assert.Equal(HttpStatusCode.Conflict, accepted.Status);
        Assert.Equal(ErrorCodes.InsufficientStock, accepted.Code);

        Assert.Equal(0, await _kit.WithDbAsync(db => db.PurchaseOrders.CountAsync(o => o.QuoteId == quoteId)));
        Assert.Equal(0, (await _kit.WithDbAsync(db => db.ProductStocks.AsNoTracking().SingleAsync(s => s.ProductId == productId))).Reserved);
        QuoteDetailResponse read = (await GetAsync(scenario.Buyer, $"{ApiBase}/quotes/{quoteId}")).As<QuoteDetailResponse>();
        Assert.Equal("Submitted", read.Status); // the quote stays open
    }

    /// <summary>
    /// A product of the supplier with live-tracked stock. There is no supplier
    /// product-upload endpoint yet, so the one catalogue row the scenario needs is
    /// written directly — a real row of the supplier's own, not a stand-in.
    /// </summary>
    private async Task<Guid> AddTrackedProductAsync(Guid supplierId, int onHand)
    {
        return await _kit.WithDbAsync(async db =>
        {
            Guid categoryId = await db.Categories.Where(c => c.Slug == "electrical").Select(c => c.PublicId).FirstAsync();
            string unique = Guid.NewGuid().ToString("N");
            Product product = new()
            {
                SupplierId = supplierId,
                CategoryId = categoryId,
                Name = "Armoured cable drum (test)",
                Slug = $"test-cable-{unique}",
                Sku = $"T-{unique[..12]}",
            };
            db.Products.Add(product);
            db.ProductStocks.Add(new ProductStock { ProductId = product.PublicId, IsStockTracked = true, OnHand = onHand });
            await db.SaveChangesAsync();
            return product.PublicId;
        });
    }
}
