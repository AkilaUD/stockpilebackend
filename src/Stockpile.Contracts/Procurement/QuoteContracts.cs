namespace Stockpile.Contracts.Procurement;

/// <summary>
/// Create the supplier's quote for one buying lead (api.md §2.7
/// <c>POST /quotes</c>, business-flows §7). The match id is the input, never the
/// quote id: the quote is created by this call, so the client cannot choose its
/// identity. No total appears here — <c>GrandTotal</c> and its parts are
/// server-computed (domain-model §6.2, architecture.md line 378 rejects a
/// writable <c>GrandTotal</c>). Optional members are omitted, never null
/// (api.md §1.4).
/// </summary>
public sealed record CreateQuoteRequest(
    Guid RfqMatchId,
    string Coverage,
    string Currency,
    int? LeadTimeDays,
    int? ValidityDays,
    string? PaymentTerms,
    string? DeliveryTerms,
    decimal? DeliveryCost,
    string TaxState,
    decimal? DiscountPercent,
    string? PackSizeNote,
    string? MoqNote,
    string? TechnicalNotes,
    string? SupplierNote,
    IReadOnlyList<CreateQuoteItemRequest> Items);

/// <summary>
/// One quoted line (api.md §2.7, domain-model §6.2 <c>QuoteRevisionItem</c>).
/// <c>RfqItemId</c> names the buyer's line the price answers, which is what makes
/// coverage checkable; <c>ProductId</c> is only needed when the supplier quotes a
/// catalogue product rather than the free-text requirement.
/// </summary>
public sealed record CreateQuoteItemRequest(
    Guid RfqItemId,
    Guid? ProductId,
    decimal Quantity,
    decimal UnitPrice,
    string? SubstitutionNote);

/// <summary>
/// The created quote (api.md §2.7). The response is deliberately the quote's
/// identity plus what the server computed: the revision pointer, the totals and
/// the derived coverage. The terms the supplier sent are readable through the
/// quote's own reads, not echoed here.
/// </summary>
public sealed record QuoteResponse(
    string PublicId,
    string QuoteNumber,
    string Status,
    int RevisionNumber,
    QuoteTotalsResponse Totals,
    QuoteCoverageResponse Coverage);

/// <summary>Server-computed money (domain-model §6.2): every value is derived from the revision's lines, never accepted from the client.</summary>
public sealed record QuoteTotalsResponse(
    decimal Subtotal,
    decimal DiscountTotal,
    decimal DeliveryCost,
    decimal TaxAmount,
    decimal GrandTotal,
    string Currency);

/// <summary>
/// What the quote actually covers (api.md §2.7): the RFQ's requested line count,
/// the lines this revision prices, and the missing line numbers by their stable
/// 1-based position (<c>RFQItem.LineNumber</c>) — which is how the buyer's
/// comparison pins an item across quotes.
/// </summary>
public sealed record QuoteCoverageResponse(
    int RequestedLines,
    int QuotedLines,
    IReadOnlyList<int> MissingLineNumbers);

/// <summary>
/// The supplier's answer to a buyer's revision request (api.md §2.7
/// <c>POST /quotes/{id}/revisions</c>, business-flows §8.2): a FULL replacement
/// of the commercial terms, never a patch — revision N is frozen the moment it is
/// created, so the new revision must state every term it offers. Totals are
/// server-computed exactly as on create; <c>RevisionKind</c> may narrow the
/// default <c>ResponseToRequest</c> to <c>PriceRevision</c>, <c>LeadTimeRevision</c>
/// or <c>ScopeRevision</c>.
/// </summary>
public sealed record ReviseQuoteRequest(
    string Coverage,
    string Currency,
    int? LeadTimeDays,
    int? ValidityDays,
    string? PaymentTerms,
    string? DeliveryTerms,
    decimal? DeliveryCost,
    string TaxState,
    decimal? DiscountPercent,
    string? PackSizeNote,
    string? MoqNote,
    string? TechnicalNotes,
    string? SupplierNote,
    string? RevisionKind,
    IReadOnlyList<CreateQuoteItemRequest> Items);

/// <summary>The buyer's typed ask on a quote (business-flows §8.1): a <c>Kind</c> plus the reason the supplier answers.</summary>
public sealed record RequestQuoteRevisionRequest(string Kind, string Reason);

/// <summary>The buyer's decline of a quote; the reason is required (business-flows §8.4).</summary>
public sealed record DeclineQuoteRequest(string Reason);

/// <summary>One row of a quote list (both the supplier's and the buyer's): identity, state and the headline terms of the current revision.</summary>
public sealed record QuoteSummaryResponse(
    string PublicId,
    string QuoteNumber,
    string RfqId,
    string RfqReference,
    string RfqTitle,
    string SupplierId,
    string SupplierName,
    string Status,
    int RevisionNumber,
    decimal GrandTotal,
    string Currency,
    int? LeadTimeDays,
    DateTime? ValidUntil,
    DateTime CreatedAt,
    DateTime? UpdatedAt);

/// <summary>One quoted line of a revision, tied back to the buyer's stable line number.</summary>
public sealed record QuoteItemResponse(
    string RfqItemId,
    int? LineNumber,
    string? ProductId,
    string ProductName,
    string? Sku,
    string Description,
    decimal Quantity,
    decimal UnitPrice,
    decimal LineTotal,
    string? SubstitutionNote);

/// <summary>
/// One immutable revision as it was offered (domain-model §6.2). <c>Status</c> is
/// derived — the current revision is <c>Active</c>, older ones <c>Superseded</c>,
/// and a withdrawn quote's current one <c>Withdrawn</c> — because a stored
/// revision row is never updated.
/// </summary>
public sealed record QuoteRevisionResponse(
    string PublicId,
    int RevisionNumber,
    string Kind,
    string Status,
    DateTime CreatedAt,
    QuoteTotalsResponse Totals,
    string TaxState,
    int? LeadTimeDays,
    DateTime? ValidUntil,
    string? PaymentTerms,
    string? DeliveryTerms,
    string? PackSizeNote,
    string? MoqNote,
    string? TechnicalNotes,
    string? SupplierNote,
    string? RespondsToMessageId,
    IReadOnlyList<QuoteItemResponse> Items);

/// <summary>One message of the negotiation thread (domain-model §6.3).</summary>
public sealed record NegotiationMessageResponse(
    string PublicId,
    string Direction,
    string Kind,
    int AgainstRevisionNumber,
    string Body,
    DateTime CreatedAt);

/// <summary>
/// A quote with its full history (api.md §2.7 <c>GET /quotes/{id}</c>). The
/// <c>Viewer</c> says which side asked (<c>supplier</c> or <c>buyer</c>). The
/// stored status is shown, except that a quote whose validity has passed reads
/// <c>Expired</c> — expiry is derived on read until the scheduler persists it.
/// </summary>
public sealed record QuoteDetailResponse(
    string PublicId,
    string QuoteNumber,
    string Viewer,
    string RfqId,
    string RfqReference,
    string RfqTitle,
    string SupplierId,
    string SupplierName,
    string Status,
    int RevisionNumber,
    DateTime? ValidUntil,
    string? DeclineReason,
    DateTime? AcceptedAt,
    DateTime? RejectedAt,
    string? OrderId,
    QuoteRevisionResponse CurrentRevision,
    IReadOnlyList<QuoteRevisionResponse> Revisions,
    IReadOnlyList<NegotiationMessageResponse> Negotiation);

/// <summary>
/// The buyer's comparison workspace for one RFQ (business-flows §9). It is
/// deliberately factual: no ranking, no "best" flag. With fewer than two live
/// quotes there is nothing to compare, so <c>Comparable</c> is false and no
/// cross-quote annotation is produced.
/// </summary>
public sealed record RfqComparisonResponse(
    string RfqId,
    string RfqReference,
    int QuoteCount,
    bool Comparable,
    IReadOnlyList<ComparisonRequestedLine> RequestedLines,
    IReadOnlyList<ComparisonQuote> Quotes,
    IReadOnlyList<ComparisonAnnotation> Annotations);

/// <summary>One RFQ line the quotes answer, pinned by its stable 1-based number.</summary>
public sealed record ComparisonRequestedLine(string RfqItemId, int LineNumber, string Description, decimal Quantity);

/// <summary>One supplier's live quote as a comparison column.</summary>
public sealed record ComparisonQuote(
    string QuoteId,
    string QuoteNumber,
    string SupplierId,
    string SupplierName,
    string Status,
    int RevisionNumber,
    QuoteTotalsResponse Totals,
    int? LeadTimeDays,
    DateTime? ValidUntil,
    string? PaymentTerms,
    string? DeliveryTerms,
    QuoteCoverageResponse Coverage,
    IReadOnlyList<QuoteItemResponse> Items);

/// <summary>A neutral fact a comparison surfaces (coverage gap, substitution offered, shortest stated lead time).</summary>
public sealed record ComparisonAnnotation(string Kind, string QuoteId, string Detail);
