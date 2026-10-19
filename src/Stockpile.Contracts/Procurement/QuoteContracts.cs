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
