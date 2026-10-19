namespace Stockpile.Domain.Enums;

/// <summary>RFQ lifecycle (domain-model §11.1). <see cref="PartiallyAwarded"/> is reserved and has no transitions until per-line award ships.</summary>
public enum RFQStatus
{
    Draft = 0,
    Submitted = 1,
    Matching = 2,
    Dispatched = 3,
    ReceivingQuotes = 4,
    Negotiating = 5,
    Awarded = 6,
    PartiallyAwarded = 7,
    Ordered = 8,
    Cancelled = 9,
    Closed = 10,
    Expired = 11,
}

/// <summary>Supplier-side match ("buying lead") lifecycle (domain-model §11.2).</summary>
public enum RFQMatchStatus
{
    Matched = 0,
    Notified = 1,
    Viewed = 2,
    Quoting = 3,
    Declined = 4,
    Expired = 5,
    Blocked = 6,
}

/// <summary>Quote lifecycle (domain-model §11.3). Accepted is terminal; accepting an Expired quote is an explicitly forbidden edge.</summary>
public enum QuoteStatus
{
    Draft = 0,
    Submitted = 1,
    UnderReview = 2,
    RevisionRequested = 3,
    Revised = 4,
    Accepted = 5,
    Rejected = 6,
    Withdrawn = 7,
    Expired = 8,
}

/// <summary>
/// How much of an RFQ a quote covers (business-flows §7). The supplier declares
/// it on submit; it is deliberately NOT a column — the stored truth is what the
/// revision's lines actually cover, and the API reports that derivation back
/// (<c>coverage{requestedLines, quotedLines, missingLineNumbers}</c>).
/// </summary>
public enum QuoteCoverage
{
    Full = 0,
    Partial = 1,
}

/// <summary>How a revision's prices treat tax (domain-model §6.2).</summary>
public enum TaxState
{
    TaxIncluded = 0,
    TaxExcluded = 1,
    TaxExempt = 2,
}

/// <summary>Why a revision was raised (domain-model §6.2).</summary>
public enum QuoteRevisionKind
{
    Initial = 0,
    PriceRevision = 1,
    LeadTimeRevision = 2,
    ScopeRevision = 3,
    ResponseToRequest = 4,
}

/// <summary>
/// Lifecycle of one immutable revision (domain-model §6.2): exactly one revision
/// is <see cref="Active"/>; superseding or withdrawing a revision never edits it.
/// </summary>
public enum QuoteRevisionStatus
{
    Active = 0,
    Superseded = 1,
    Withdrawn = 2,
}

/// <summary>Purchase order lifecycle (domain-model §11.4).</summary>
public enum PurchaseOrderStatus
{
    Draft = 0,
    Confirmed = 1,
    SupplierAcknowledged = 2,
    Processing = 3,
    PartiallyFulfilled = 4,
    Fulfilled = 5,
    Cancelled = 6,
    Disputed = 7,
}

/// <summary>Supplier verification lifecycle (domain-model §11.5).</summary>
public enum SupplierStatus
{
    Draft = 0,
    PendingVerification = 1,
    Active = 2,
    Suspended = 3,
    Closed = 4,
}

/// <summary>Product publication lifecycle (domain-model §11.6).</summary>
public enum ProductStatus
{
    Draft = 0,
    PendingReview = 1,
    Published = 2,
    Unpublished = 3,
    Rejected = 4,
    Archived = 5,
}

/// <summary>Subscription billing lifecycle (domain-model §11.7).</summary>
public enum SubscriptionStatus
{
    Trial = 0,
    Active = 1,
    PastDue = 2,
    GracePeriod = 3,
    Suspended = 4,
    Cancelled = 5,
    Expired = 6,
}
