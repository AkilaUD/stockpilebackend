using Stockpile.Application.Audit;
using Stockpile.Application.Authentication;
using Stockpile.Application.Common.Errors;
using Stockpile.Application.Organizations;
using Stockpile.Application.Suppliers;
using Stockpile.Contracts.Errors;
using Stockpile.Contracts.Orders;
using Stockpile.Contracts.Permissions;
using Stockpile.Domain.Entities.Catalogue;
using Stockpile.Domain.Entities.Orders;
using Stockpile.Domain.Entities.Procurement;
using Stockpile.Domain.Enums;
using Stockpile.Domain.StateMachines;

namespace Stockpile.Application.Orders;

/// <summary>
/// Quote acceptance and the order surface (api.md §2.7/§2.8, business-flows §10).
/// <para>
/// <b>Accept is one transaction.</b> Eligibility (the buyer's organisation owns the
/// RFQ, the quote is open, the RFQ is open), validity (an elapsed
/// <c>ValidUntil</c> is <c>QUOTE_EXPIRED</c>), stock (tracked stock only), the
/// order and its lines (server totals copied from the accepted revision), the
/// quote → <c>Accepted</c> and RFQ → <c>Awarded</c> → <c>Ordered</c> edges, the
/// audit rows and the outbox events all commit together through the store's single
/// save, or nothing does. The endpoint is <c>[Idempotent]</c>, so a retry of the
/// same key replays the original order id; a second, differently-keyed accept —
/// concurrent or not — meets the quote's rowversion and the one-order-per-quote
/// unique index and answers <c>QUOTE_ALREADY_ACCEPTED</c>.
/// </para>
/// <para>
/// Payment and delivery happen outside the marketplace (business-flows §10): the
/// order is a record and fulfilment tracker, not a payment.
/// </para>
/// </summary>
public sealed class OrderService(
    IOrdersStore orders,
    IOrganizationStore organizations,
    ISupplierSelfStore suppliers,
    IUserPermissionStore permissions,
    IAuditService audit,
    ICurrentRequestContext requestContext)
{
    private static readonly StateMachine<QuoteStatus> QuoteMachine = new(TransitionTables.Quote.All);
    private static readonly StateMachine<RFQStatus> RfqMachine = new(TransitionTables.Rfq.All);
    private static readonly StateMachine<PurchaseOrderStatus> OrderMachine = new(TransitionTables.PurchaseOrder.All);

    /// <summary>Cap on an order list page: lists are newest-first and bounded until cursors land.</summary>
    private const int ListLimit = 100;

    /// <summary>Accepts a quote and creates its order (api.md §2.7 <c>POST /quotes/{id}/accept</c>).</summary>
    public async Task<AcceptQuoteResponse> AcceptQuoteAsync(
        Guid userId, Guid quoteId, CancellationToken cancellationToken)
    {
        HashSet<Guid> organizationIds = await organizations.GetAcceptedOrganizationIdsAsync(userId, cancellationToken);
        if (organizationIds.Count == 0)
        {
            throw NotFound();
        }

        QuoteAcceptance acceptance = await orders.LoadAcceptanceAsync(organizationIds, quoteId, cancellationToken)
            ?? throw NotFound();
        Quote quote = acceptance.Quote;
        Rfq rfq = acceptance.Rfq;
        DateTime now = DateTime.UtcNow;

        // 1. Eligibility. An order already exists for this quote → it was accepted.
        RequireAcceptable(quote, acceptance.ExistingOrderId, now);

        HashSet<string> granted = (await permissions.GetPermissionCodesAsync(userId, cancellationToken))
            .ToHashSet(StringComparer.Ordinal);
        ActorContext buyer = ActorContext.ForUser(userId, granted);

        // 2. The RFQ walks to Ordered under the platform's own edges: the buyer's
        //    capability drives the award, the system drives the rest (§11.1).
        List<RfqStatusHistory> rfqHistory = [];
        string? correlationId = requestContext.CorrelationId;
        if (rfq.Status == RFQStatus.Dispatched)
        {
            ApplyRfq(rfq, RFQStatus.ReceivingQuotes, ActorContext.System, rfqHistory, userId, correlationId);
        }

        ApplyRfq(rfq, RFQStatus.Awarded, buyer, rfqHistory, userId, correlationId);
        ApplyRfq(rfq, RFQStatus.Ordered, ActorContext.System, rfqHistory, userId, correlationId);
        rfq.UpdatedAt = now;
        rfq.UpdatedBy = userId;

        // 3. The quote → Accepted through its own edge (buyer org must own the RFQ — already proven above).
        TransitionResult<QuoteStatus> accepted = QuoteMachine.Evaluate(quote.Status, QuoteStatus.Accepted, buyer, reason: null);
        if (!accepted.IsAllowed)
        {
            throw accepted.Kind == TransitionResultKind.PermissionDenied
                ? new AppException(ErrorCodes.InsufficientCapability)
                : new AppException(ErrorCodes.StateTransitionInvalid);
        }

        quote.Status = accepted.Edge!.To;
        quote.AcceptedAt = now;
        quote.UpdatedAt = now;
        quote.UpdatedBy = userId;

        // 4. The order: every figure is the accepted revision's, never recomputed.
        QuoteRevision revision = acceptance.Revision;
        PurchaseOrder order = new()
        {
            OrderNumber = await NextOrderNumberAsync(cancellationToken),
            RfqId = rfq.PublicId,
            QuoteId = quote.PublicId,
            QuoteRevisionId = revision.PublicId,
            SupplierId = quote.SupplierId,
            BuyerOrganizationId = rfq.OrganizationId,
            CurrencyCode = revision.CurrencyCode,
            Subtotal = revision.Subtotal,
            DiscountTotal = revision.DiscountTotal,
            DeliveryCost = revision.DeliveryCost,
            TaxAmount = revision.TaxAmount,
            GrandTotal = revision.GrandTotal,
            LeadTimeDays = revision.LeadTimeDays,
            PaymentTerms = revision.PaymentTerms,
            DeliveryTerms = revision.DeliveryTerms,
            DeliveryLocationText = rfq.DeliveryLocationText,
            RequestedDeliveryAt = rfq.RequestedDeliveryAt,
            CreatedBy = userId,
            CreatedAt = now,
        };

        List<PurchaseOrderItem> items = BuildItems(order, acceptance);

        // 5. Stock (tracked stock only): hold what was ordered, or refuse with the
        //    specific availability. OnHand − Reserved never goes below zero here, and
        //    the CHECK constraint is the final guard.
        ReserveStock(items, acceptance.TrackedStock, now);
        order.StockReservation = items.Any(item => item.StockState == StockReservationState.Held)
            ? StockReservationState.Held
            : StockReservationState.NotRequired;

        // 6. Draft → Confirmed under the buyer's own order.create capability (§11.4).
        TransitionResult<PurchaseOrderStatus> confirmed = OrderMachine.Evaluate(
            order.Status, PurchaseOrderStatus.Confirmed, buyer, reason: null);
        if (!confirmed.IsAllowed)
        {
            throw confirmed.Kind == TransitionResultKind.PermissionDenied
                ? new AppException(ErrorCodes.InsufficientCapability)
                : new AppException(ErrorCodes.OrderCreateFailed);
        }

        PurchaseOrderStatus from = order.Status;
        order.Status = confirmed.Edge!.To;
        order.ConfirmedAt = now;

        List<PurchaseOrderStatusHistory> history =
        [
            new()
            {
                PurchaseOrderId = order.PublicId,
                FromState = from,
                ToState = order.Status,
                ActorId = userId,
                CorrelationId = correlationId,
                OccurredAt = now,
            },
        ];

        // 7. Outbox events ride the aggregates' own save (the interceptor flushes them in it).
        quote.Raise("quote.accepted", userId, correlationId, new
        {
            quoteId = quote.PublicId,
            rfqId = rfq.PublicId,
            supplierId = quote.SupplierId,
            revisionNumber = revision.RevisionNumber,
            orderId = order.PublicId,
        });
        order.Raise("order.created", userId, correlationId, new
        {
            orderId = order.PublicId,
            quoteId = quote.PublicId,
            rfqId = rfq.PublicId,
            supplierId = order.SupplierId,
            buyerOrganizationId = order.BuyerOrganizationId,
            stockReservation = order.StockReservation.ToString(),
        });

        await orders.AddOrderAsync(order, items, history, rfqHistory, cancellationToken);

        // Under [Idempotent] the request's transaction is open, so these rows commit
        // with the order — atomic with the change they describe.
        await audit.RecordAsync(
            new AuditEntry("quote.accepted", "Quote", quote.PublicId.ToString(), AuditResult.Success),
            cancellationToken);
        await audit.RecordAsync(
            new AuditEntry("order.created", "PurchaseOrder", order.PublicId.ToString(), AuditResult.Success),
            cancellationToken);

        return new AcceptQuoteResponse(
            order.PublicId.ToString(),
            order.OrderNumber,
            order.Status.ToString(),
            order.StockReservation.ToString());
    }

    /// <summary>The caller's organisations' orders, newest first (api.md §2.8 <c>GET /me/orders</c>).</summary>
    public async Task<IReadOnlyList<OrderSummaryResponse>> ListForBuyerAsync(
        Guid userId, CancellationToken cancellationToken)
    {
        HashSet<Guid> organizationIds = await organizations.GetAcceptedOrganizationIdsAsync(userId, cancellationToken);
        if (organizationIds.Count == 0)
        {
            return [];
        }

        return (await orders.ListForBuyerAsync(organizationIds, ListLimit, cancellationToken))
            .Select(ToSummary)
            .ToList();
    }

    /// <summary>The caller's supplier's orders, newest first (api.md §2.9 <c>GET /suppliers/me/orders</c>).</summary>
    public async Task<IReadOnlyList<OrderSummaryResponse>> ListForSupplierAsync(
        Guid userId, CancellationToken cancellationToken)
    {
        Guid supplierId = await suppliers.ResolveSupplierIdAsync(userId, cancellationToken)
            ?? throw NotFound();

        return (await orders.ListForSupplierAsync(supplierId, ListLimit, cancellationToken))
            .Select(ToSummary)
            .ToList();
    }

    /// <summary>
    /// One order, as the supplier it was placed with or as a member of the buying
    /// organisation — and nothing else: every other id answers the one 404 of
    /// <c>api.md §1.6</c>.
    /// </summary>
    public async Task<OrderDetailResponse> GetAsync(Guid userId, Guid orderId, CancellationToken cancellationToken)
    {
        HashSet<string> granted = (await permissions.GetPermissionCodesAsync(userId, cancellationToken))
            .ToHashSet(StringComparer.Ordinal);
        bool asSupplier = granted.Contains(Permissions.OrderFulfill);
        bool asBuyer = granted.Contains(Permissions.OrderReadOwn);
        if (!asSupplier && !asBuyer)
        {
            throw new AppException(ErrorCodes.InsufficientCapability);
        }

        if (asSupplier
            && await suppliers.ResolveSupplierIdAsync(userId, cancellationToken) is Guid supplierId
            && await orders.LoadForSupplierAsync(supplierId, orderId, forUpdate: false, cancellationToken) is { } supplierView)
        {
            return ToDetail(supplierView);
        }

        if (asBuyer)
        {
            HashSet<Guid> organizationIds = await organizations.GetAcceptedOrganizationIdsAsync(userId, cancellationToken);
            if (organizationIds.Count > 0
                && await orders.LoadForBuyerAsync(organizationIds, orderId, forUpdate: false, cancellationToken) is { } buyerView)
            {
                return ToDetail(buyerView);
            }
        }

        throw NotFound();
    }

    /// <summary>
    /// The supplier acknowledges a confirmed order (<c>Confirmed → SupplierAcknowledged</c>,
    /// capability <c>order.fulfill</c>, §11.4). Only the order's own supplier can; a
    /// foreign id is the 404 of any other order read.
    /// </summary>
    public async Task<OrderDetailResponse> AcknowledgeAsync(
        Guid userId, Guid orderId, CancellationToken cancellationToken)
    {
        Guid supplierId = await suppliers.ResolveSupplierIdAsync(userId, cancellationToken)
            ?? throw NotFound();
        OrderDetail detail = await orders.LoadForSupplierAsync(supplierId, orderId, forUpdate: true, cancellationToken)
            ?? throw NotFound();
        PurchaseOrder order = detail.Record.Order;

        HashSet<string> granted = (await permissions.GetPermissionCodesAsync(userId, cancellationToken))
            .ToHashSet(StringComparer.Ordinal);
        TransitionResult<PurchaseOrderStatus> result = OrderMachine.Evaluate(
            order.Status, PurchaseOrderStatus.SupplierAcknowledged, ActorContext.ForUser(userId, granted), reason: null);
        if (!result.IsAllowed)
        {
            throw result.Kind == TransitionResultKind.PermissionDenied
                ? new AppException(ErrorCodes.InsufficientCapability)
                : new AppException(ErrorCodes.StateTransitionInvalid);
        }

        DateTime now = DateTime.UtcNow;
        PurchaseOrderStatus from = order.Status;
        order.Status = result.Edge!.To;
        order.AcknowledgedAt = now;
        order.UpdatedAt = now;
        order.UpdatedBy = userId;
        order.Raise("order.acknowledged", userId, requestContext.CorrelationId, new
        {
            orderId = order.PublicId,
            quoteId = order.QuoteId,
            supplierId = order.SupplierId,
        });

        PurchaseOrderStatusHistory entry = new()
        {
            PurchaseOrderId = order.PublicId,
            FromState = from,
            ToState = order.Status,
            ActorId = userId,
            CorrelationId = requestContext.CorrelationId,
            OccurredAt = now,
        };

        await orders.SaveTransitionAsync(order, entry, cancellationToken);
        await audit.RecordAsync(
            new AuditEntry("order.acknowledged", "PurchaseOrder", order.PublicId.ToString(), AuditResult.Success),
            cancellationToken);

        return ToDetail(detail with { History = [.. detail.History, entry] });
    }

    // ---- Internals ----------------------------------------------------------

    /// <summary>
    /// A quote is acceptable only while it is an open offer: an accepted one (or one
    /// that already produced an order) answers its own conflict, a lapsed one is
    /// <c>QUOTE_EXPIRED</c> (§64: an expired quote is never accepted), and a
    /// rejected or withdrawn one is immutable. A quote awaiting the supplier's
    /// revision has no accept edge and falls through to the state machine.
    /// </summary>
    private static void RequireAcceptable(Quote quote, Guid? existingOrderId, DateTime now)
    {
        if (existingOrderId is not null || quote.Status == QuoteStatus.Accepted)
        {
            throw new AppException(ErrorCodes.QuoteAlreadyAccepted);
        }

        if (quote.Status == QuoteStatus.Expired || IsPastValidity(quote, now))
        {
            throw new AppException(ErrorCodes.QuoteExpired);
        }

        if (quote.Status is QuoteStatus.Rejected or QuoteStatus.Withdrawn)
        {
            throw new AppException(ErrorCodes.QuoteImmutable);
        }
    }

    private static bool IsPastValidity(Quote quote, DateTime now)
        => quote.ValidUntil is DateTime validUntil && validUntil <= now;

    private static void ApplyRfq(
        Rfq rfq,
        RFQStatus to,
        ActorContext actor,
        List<RfqStatusHistory> history,
        Guid userId,
        string? correlationId)
    {
        TransitionResult<RFQStatus> result = RfqMachine.Evaluate(rfq.Status, to, actor, reason: null);
        if (!result.IsAllowed)
        {
            throw result.Kind == TransitionResultKind.PermissionDenied
                ? new AppException(ErrorCodes.InsufficientCapability)
                : new AppException(ErrorCodes.StateTransitionInvalid);
        }

        history.Add(new RfqStatusHistory
        {
            RfqId = rfq.PublicId,
            FromState = rfq.Status,
            ToState = result.Edge!.To,
            ActorId = actor.IsSystem ? null : userId,
            CorrelationId = correlationId,
        });
        rfq.Status = result.Edge.To;
    }

    private static List<PurchaseOrderItem> BuildItems(PurchaseOrder order, QuoteAcceptance acceptance)
    {
        List<PurchaseOrderItem> items = [];
        int lineNumber = 0;

        foreach (QuoteRevisionItem source in acceptance.Items
                     .OrderBy(item => item.RfqItemId is Guid id && acceptance.LineNumbers.TryGetValue(id, out int line)
                         ? line
                         : int.MaxValue))
        {
            items.Add(new PurchaseOrderItem
            {
                PurchaseOrderId = order.PublicId,
                LineNumber = ++lineNumber,
                ProductId = source.ProductId,
                ProductNameSnapshot = source.ProductNameSnapshot,
                SkuSnapshot = source.SkuSnapshot,
                Description = source.Description,
                Quantity = source.Quantity,
                UnitOfMeasureId = source.UnitOfMeasureId,
                UnitPrice = source.UnitPrice,
                LineDiscountPct = source.LineDiscountPct,
                LineTotal = source.LineTotal,
                StockState = StockReservationState.NotRequired,
            });
        }

        return items;
    }

    /// <summary>
    /// Holds tracked stock for each line (business-flows §10 step 5, policy
    /// <c>Reserve</c>): <c>Reserved += quantity</c>, never past <c>OnHand</c>.
    /// Untracked stock (migrated catalogue lines, §4.5) is not held — that is the
    /// honest reading of "supplier-declared availability", not a silent success.
    /// A shortfall states the figure the buyer must act on. Reserving does not
    /// change <c>OnHand</c>, so no <c>StockAdjustment</c> row is written.
    /// </summary>
    private static void ReserveStock(
        List<PurchaseOrderItem> items, IReadOnlyDictionary<Guid, ProductStock> trackedStock, DateTime now)
    {
        foreach (PurchaseOrderItem item in items)
        {
            if (item.ProductId is not Guid productId || !trackedStock.TryGetValue(productId, out ProductStock? stock))
            {
                continue;
            }

            decimal needed = Math.Ceiling(item.Quantity);
            int available = stock.OnHand - stock.Reserved;
            if (needed > available)
            {
                throw new AppException(
                    ErrorCodes.InsufficientStock,
                    [
                        new FieldError(
                            $"items[{item.LineNumber - 1}].quantity",
                            "insufficient_stock",
                            $"Only {Math.Max(available, 0)} available for this line."),
                    ],
                    $"There is not enough stock for line {item.LineNumber}: {Math.Max(available, 0)} available.");
            }

            stock.Reserved += (int)needed;
            stock.LastMovementAt = now;
            item.StockState = StockReservationState.Held;
        }
    }

    private async Task<string> NextOrderNumberAsync(CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            string code = $"PO-{DateTime.UtcNow.Year}-{Guid.NewGuid():N}"[..17].ToUpperInvariant();
            if (!await orders.OrderNumberExistsAsync(code, cancellationToken))
            {
                return code;
            }
        }

        throw new AppException(ErrorCodes.InternalError);
    }

    private static OrderSummaryResponse ToSummary(OrderRecord record)
    {
        PurchaseOrder order = record.Order;
        return new OrderSummaryResponse(
            order.PublicId.ToString(),
            order.OrderNumber,
            order.QuoteId.ToString(),
            order.RfqId.ToString(),
            order.SupplierId.ToString(),
            record.SupplierName,
            record.BuyerName,
            order.Status.ToString(),
            order.GrandTotal,
            order.CurrencyCode,
            order.StockReservation.ToString(),
            order.CreatedAt);
    }

    private static OrderDetailResponse ToDetail(OrderDetail detail)
    {
        OrderRecord record = detail.Record;
        PurchaseOrder order = record.Order;
        return new OrderDetailResponse(
            order.PublicId.ToString(),
            order.OrderNumber,
            order.QuoteId.ToString(),
            record.QuoteNumber,
            record.QuoteRevisionNumber,
            order.RfqId.ToString(),
            order.SupplierId.ToString(),
            record.SupplierName,
            record.BuyerName,
            order.Status.ToString(),
            order.CurrencyCode,
            order.Subtotal,
            order.DiscountTotal,
            order.DeliveryCost,
            order.TaxAmount,
            order.GrandTotal,
            order.LeadTimeDays,
            order.PaymentTerms,
            order.DeliveryTerms,
            order.DeliveryLocationText,
            order.RequestedDeliveryAt,
            order.StockReservation.ToString(),
            order.CreatedAt,
            order.ConfirmedAt,
            order.AcknowledgedAt,
            detail.Items
                .OrderBy(item => item.LineNumber)
                .Select(item => new OrderItemResponse(
                    item.LineNumber,
                    item.ProductId?.ToString(),
                    item.ProductNameSnapshot,
                    item.SkuSnapshot,
                    item.Description,
                    item.Quantity,
                    item.UnitPrice,
                    item.LineTotal,
                    item.StockState.ToString()))
                .ToList(),
            detail.History
                .OrderBy(entry => entry.OccurredAt)
                .Select(entry => new OrderStatusEntryResponse(
                    entry.FromState.ToString(), entry.ToState.ToString(), entry.OccurredAt, entry.Reason))
                .ToList());
    }

    private static AppException NotFound() => new(ErrorCodes.ResourceNotFound);
}
