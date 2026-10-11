using Stockpile.Domain.Enums;

namespace Stockpile.Domain.StateMachines;

/// <summary>
/// The full transition tables of domain-model §11 — every workflow of Phase 1–8,
/// seeded even where handlers arrive later (implementation-plan 1.16).
/// Single source of truth: <c>StateMachine.Evaluate()</c> reads these, and the
/// <c>StateTransition</c> table is seeded from them so the admin UI shows the
/// same truth. Permission codes are literals matching
/// <c>Stockpile.Contracts.Permissions.Permissions</c>; an architecture test
/// fails the build if any code drifts out of that set.
/// </summary>
public static class TransitionTables
{
    /// <summary>domain-model §11.1. Note: <c>PartiallyAwarded</c> is reserved and intentionally has no edges until per-line award ships.</summary>
    public static class Rfq
    {
        public const string EntityName = "RFQ";

        public static readonly IReadOnlyList<StateTransitionDef<RFQStatus>> All =
        [
            new(RFQStatus.Draft, RFQStatus.Submitted, "rfq.submit"),
            new(RFQStatus.Submitted, RFQStatus.Matching, "rfq.match", Notes: "system"),
            new(RFQStatus.Matching, RFQStatus.Dispatched, "rfq.match", Notes: "system"),
            new(RFQStatus.Dispatched, RFQStatus.ReceivingQuotes, "rfq.match", Notes: "system"),
            new(RFQStatus.ReceivingQuotes, RFQStatus.Negotiating, "quote.accept", Notes: "system"),
            new(RFQStatus.ReceivingQuotes, RFQStatus.Awarded, "rfq.award"),
            new(RFQStatus.Negotiating, RFQStatus.Awarded, "rfq.award"),
            new(RFQStatus.Awarded, RFQStatus.Ordered, "order.create", Notes: "system"),
            new(RFQStatus.Draft, RFQStatus.Cancelled, "rfq.cancel", RequiresReason: true),
            new(RFQStatus.Submitted, RFQStatus.Cancelled, "rfq.cancel", RequiresReason: true),
            new(RFQStatus.ReceivingQuotes, RFQStatus.Cancelled, "rfq.cancel", RequiresReason: true),
            new(RFQStatus.Negotiating, RFQStatus.Cancelled, "rfq.cancel", RequiresReason: true),
            new(RFQStatus.ReceivingQuotes, RFQStatus.Closed, "rfq.close"),
            new(RFQStatus.Negotiating, RFQStatus.Closed, "rfq.close"),
            new(RFQStatus.Awarded, RFQStatus.Closed, "rfq.close"),
            new(RFQStatus.ReceivingQuotes, RFQStatus.Expired, Notes: "system (scheduler)"),
            new(RFQStatus.Negotiating, RFQStatus.Expired, Notes: "system (scheduler)"),
        ];
    }

    /// <summary>domain-model §11.2.</summary>
    public static class RfqMatch
    {
        public const string EntityName = "RFQMatch";

        public static readonly IReadOnlyList<StateTransitionDef<RFQMatchStatus>> All =
        [
            new(RFQMatchStatus.Matched, RFQMatchStatus.Notified, Notes: "system"),
            new(RFQMatchStatus.Notified, RFQMatchStatus.Viewed, Notes: "system"),
            new(RFQMatchStatus.Viewed, RFQMatchStatus.Quoting, Notes: "system"),
            new(null, RFQMatchStatus.Declined, "rfq.match.decline", RequiresReason: true, Notes: "any → Declined (reason required)"),
            new(RFQMatchStatus.Matched, RFQMatchStatus.Expired, Notes: "system"),
            new(RFQMatchStatus.Notified, RFQMatchStatus.Expired, Notes: "system"),
            new(RFQMatchStatus.Matched, RFQMatchStatus.Blocked, "rfq.match.moderate", RequiresReason: true, Notes: "admin"),
            new(RFQMatchStatus.Notified, RFQMatchStatus.Blocked, "rfq.match.moderate", RequiresReason: true, Notes: "admin"),
            new(RFQMatchStatus.Viewed, RFQMatchStatus.Blocked, "rfq.match.moderate", RequiresReason: true, Notes: "admin"),
        ];
    }

    /// <summary>domain-model §11.3 — accepting an Expired quote is an explicitly forbidden edge (§64).</summary>
    public static class Quote
    {
        public const string EntityName = "Quote";

        public static readonly IReadOnlyList<StateTransitionDef<QuoteStatus>> All =
        [
            new(QuoteStatus.Draft, QuoteStatus.Submitted, "quote.submit"),
            new(QuoteStatus.Submitted, QuoteStatus.UnderReview, "quote.read.buyer", Notes: "system"),
            new(QuoteStatus.Submitted, QuoteStatus.RevisionRequested, "quote.requestRevision", RequiresReason: true),
            new(QuoteStatus.UnderReview, QuoteStatus.RevisionRequested, "quote.requestRevision", RequiresReason: true),
            new(QuoteStatus.RevisionRequested, QuoteStatus.Revised, "quote.revise"),
            new(QuoteStatus.Submitted, QuoteStatus.Accepted, "quote.accept"),
            new(QuoteStatus.UnderReview, QuoteStatus.Accepted, "quote.accept"),
            new(QuoteStatus.Revised, QuoteStatus.Accepted, "quote.accept"),
            new(QuoteStatus.Submitted, QuoteStatus.Rejected, "quote.decline"),
            new(QuoteStatus.UnderReview, QuoteStatus.Rejected, "quote.decline"),
            new(QuoteStatus.Revised, QuoteStatus.Rejected, "quote.decline"),
            new(QuoteStatus.Submitted, QuoteStatus.Withdrawn, "quote.withdraw"),
            new(QuoteStatus.UnderReview, QuoteStatus.Withdrawn, "quote.withdraw"),
            new(QuoteStatus.Revised, QuoteStatus.Withdrawn, "quote.withdraw"),
            new(QuoteStatus.Submitted, QuoteStatus.Expired, Notes: "system (ValidUntil passed)"),
            new(QuoteStatus.UnderReview, QuoteStatus.Expired, Notes: "system (ValidUntil passed)"),
            new(QuoteStatus.Revised, QuoteStatus.Expired, Notes: "system (ValidUntil passed)"),
            new(QuoteStatus.Expired, QuoteStatus.Accepted, "quote.accept", IsForbidden: true, Notes: "accepting an expired quote is rejected (§64)"),
        ];
    }

    /// <summary>domain-model §11.4.</summary>
    public static class PurchaseOrder
    {
        public const string EntityName = "PurchaseOrder";

        public static readonly IReadOnlyList<StateTransitionDef<PurchaseOrderStatus>> All =
        [
            new(PurchaseOrderStatus.Draft, PurchaseOrderStatus.Confirmed, "order.create"),
            new(PurchaseOrderStatus.Confirmed, PurchaseOrderStatus.SupplierAcknowledged, "order.fulfill"),
            new(PurchaseOrderStatus.SupplierAcknowledged, PurchaseOrderStatus.Processing, "order.fulfill"),
            new(PurchaseOrderStatus.Processing, PurchaseOrderStatus.PartiallyFulfilled, "order.fulfill"),
            new(PurchaseOrderStatus.PartiallyFulfilled, PurchaseOrderStatus.Fulfilled, "order.fulfill"),
            new(PurchaseOrderStatus.Draft, PurchaseOrderStatus.Cancelled, "order.cancel", RequiresReason: true),
            new(PurchaseOrderStatus.Confirmed, PurchaseOrderStatus.Cancelled, "order.cancel", RequiresReason: true),
            new(PurchaseOrderStatus.SupplierAcknowledged, PurchaseOrderStatus.Cancelled, "order.cancel", RequiresReason: true),
            new(PurchaseOrderStatus.Processing, PurchaseOrderStatus.Cancelled, "order.cancel", RequiresReason: true),
            new(PurchaseOrderStatus.PartiallyFulfilled, PurchaseOrderStatus.Cancelled, "order.cancel", RequiresReason: true),
            new(PurchaseOrderStatus.Confirmed, PurchaseOrderStatus.Disputed, "order.dispute", RequiresReason: true),
            new(PurchaseOrderStatus.SupplierAcknowledged, PurchaseOrderStatus.Disputed, "order.dispute", RequiresReason: true),
            new(PurchaseOrderStatus.Processing, PurchaseOrderStatus.Disputed, "order.dispute", RequiresReason: true),
        ];
    }

    /// <summary>domain-model §11.5 — suspension immediately removes the supplier from matching.</summary>
    public static class Supplier
    {
        public const string EntityName = "Supplier";

        public static readonly IReadOnlyList<StateTransitionDef<SupplierStatus>> All =
        [
            new(SupplierStatus.Draft, SupplierStatus.PendingVerification, "supplier.update.own", Notes: "supplier submits profile"),
            new(SupplierStatus.PendingVerification, SupplierStatus.Active, "supplier.verify"),
            new(SupplierStatus.Active, SupplierStatus.Suspended, "supplier.suspend", RequiresReason: true),
            new(SupplierStatus.Suspended, SupplierStatus.Active, "supplier.verify"),
            new(SupplierStatus.Active, SupplierStatus.Closed, "supplier.suspend", RequiresReason: true),
            new(SupplierStatus.Suspended, SupplierStatus.Closed, "supplier.suspend", RequiresReason: true),
        ];
    }

    /// <summary>domain-model §11.6 — Rejected's only edge is Archived; rejection notifies the supplier.</summary>
    public static class Product
    {
        public const string EntityName = "Product";

        public static readonly IReadOnlyList<StateTransitionDef<ProductStatus>> All =
        [
            new(ProductStatus.Draft, ProductStatus.PendingReview, "product.publish", Notes: "supplier submits for review"),
            new(ProductStatus.PendingReview, ProductStatus.Published, "product.moderate"),
            new(ProductStatus.Draft, ProductStatus.Rejected, "product.moderate", RequiresReason: true, Notes: "notify supplier"),
            new(ProductStatus.PendingReview, ProductStatus.Rejected, "product.moderate", RequiresReason: true, Notes: "notify supplier"),
            new(ProductStatus.Published, ProductStatus.Unpublished, "product.update.own"),
            new(ProductStatus.Unpublished, ProductStatus.Published, "product.publish"),
            new(ProductStatus.Draft, ProductStatus.Archived, "product.update.own"),
            new(ProductStatus.Unpublished, ProductStatus.Archived, "product.update.own"),
            new(ProductStatus.Rejected, ProductStatus.Archived, "product.update.own"),
        ];
    }

    /// <summary>domain-model §11.7 — billing edges without a permission are payment/scheduler driven.</summary>
    public static class Subscription
    {
        public const string EntityName = "Subscription";

        public static readonly IReadOnlyList<StateTransitionDef<SubscriptionStatus>> All =
        [
            new(SubscriptionStatus.Trial, SubscriptionStatus.Active, Notes: "system (payment)"),
            new(SubscriptionStatus.Active, SubscriptionStatus.PastDue, Notes: "system (billing sweep)"),
            new(SubscriptionStatus.PastDue, SubscriptionStatus.GracePeriod, Notes: "system (billing sweep)"),
            new(SubscriptionStatus.GracePeriod, SubscriptionStatus.Suspended, Notes: "system (billing sweep)"),
            new(SubscriptionStatus.Active, SubscriptionStatus.Cancelled, "subscription.manage"),
            new(SubscriptionStatus.PastDue, SubscriptionStatus.Cancelled, "subscription.manage"),
            new(SubscriptionStatus.GracePeriod, SubscriptionStatus.Cancelled, "subscription.manage"),
            new(SubscriptionStatus.Suspended, SubscriptionStatus.Expired, Notes: "system"),
            new(SubscriptionStatus.GracePeriod, SubscriptionStatus.Expired, Notes: "system"),
            new(SubscriptionStatus.Expired, SubscriptionStatus.Active, Notes: "system (renewal)"),
        ];
    }

    /// <summary>Every machine's rows in seed order (task 1.16).</summary>
    public static IReadOnlyList<(string EntityName, StateTransitionRow Row)> AllMachines()
    {
        List<(string, StateTransitionRow)> rows = [];

        void Add<TState>(string entity, IReadOnlyList<StateTransitionDef<TState>> defs)
            where TState : struct, Enum
        {
            foreach (StateTransitionDef<TState> def in defs)
            {
                rows.Add((entity, new StateTransitionRow(
                    From: def.From?.ToString() ?? "*",
                    To: def.To.ToString()!,
                    PermissionCode: def.PermissionCode,
                    RequiresReason: def.RequiresReason,
                    IsForbidden: def.IsForbidden,
                    Notes: def.Notes)));
            }
        }

        Add(Rfq.EntityName, Rfq.All);
        Add(RfqMatch.EntityName, RfqMatch.All);
        Add(Quote.EntityName, Quote.All);
        Add(PurchaseOrder.EntityName, PurchaseOrder.All);
        Add(Supplier.EntityName, Supplier.All);
        Add(Product.EntityName, Product.All);
        Add(Subscription.EntityName, Subscription.All);

        return rows;
    }
}

/// <summary>Erased transition row ready to be written to <c>StateTransition</c>.</summary>
public sealed record StateTransitionRow(
    string From,
    string To,
    string? PermissionCode,
    bool RequiresReason,
    bool IsForbidden,
    string? Notes);
