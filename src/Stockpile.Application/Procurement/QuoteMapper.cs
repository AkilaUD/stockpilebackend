using Stockpile.Contracts.Procurement;
using Stockpile.Domain.Entities.Procurement;
using Stockpile.Domain.Enums;

namespace Stockpile.Application.Procurement;

/// <summary>
/// Read-side projection of quotes (api.md §2.7). Two facts are derived here, never
/// stored, because the stored rows are append-only or lazily maintained:
/// <list type="bullet">
/// <item>a revision's <c>Status</c> (current = Active, older = Superseded, a
/// withdrawn quote's current one = Withdrawn) — revision rows are never updated;</item>
/// <item>a quote's effective <c>Expired</c> status once its validity has passed —
/// the scheduler that would persist it is a later phase.</item>
/// </list>
/// </summary>
internal static class QuoteMapper
{
    /// <summary>The statuses an elapsed <c>ValidUntil</c> turns into <c>Expired</c> (domain-model §11.3).</summary>
    private static bool Expirable(QuoteStatus status)
        => status is QuoteStatus.Submitted or QuoteStatus.UnderReview or QuoteStatus.Revised;

    /// <summary>True when the quote's validity has passed and it is still waiting on a decision.</summary>
    public static bool IsPastValidity(Quote quote, DateTime now)
        => Expirable(quote.Status) && quote.ValidUntil is DateTime validUntil && validUntil <= now;

    /// <summary>The status a reader should see: the stored one, or <c>Expired</c> when validity has lapsed.</summary>
    public static QuoteStatus EffectiveStatus(Quote quote, DateTime now)
        => IsPastValidity(quote, now) ? QuoteStatus.Expired : quote.Status;

    public static QuoteSummaryResponse ToSummary(QuoteDetail detail, DateTime now)
    {
        Quote quote = detail.Quote;
        QuoteRevision current = detail.Current.Revision;
        return new QuoteSummaryResponse(
            quote.PublicId.ToString(),
            quote.QuoteNumber,
            detail.Rfq.PublicId.ToString(),
            detail.Rfq.ReferenceCode,
            detail.Rfq.Title,
            quote.SupplierId.ToString(),
            detail.SupplierName,
            EffectiveStatus(quote, now).ToString(),
            quote.CurrentRevisionNumber,
            current.GrandTotal,
            current.CurrencyCode,
            current.LeadTimeDays,
            quote.ValidUntil,
            quote.CreatedAt,
            quote.UpdatedAt);
    }

    public static QuoteDetailResponse ToDetail(QuoteDetail detail, string viewer, DateTime now)
    {
        Quote quote = detail.Quote;
        Dictionary<Guid, int> lineNumbers = detail.Lines.ToDictionary(line => line.Item.PublicId, line => line.Item.LineNumber);

        List<QuoteRevisionResponse> revisions = detail.Revisions
            .OrderBy(revision => revision.Revision.RevisionNumber)
            .Select(revision => ToRevision(revision, quote, lineNumbers))
            .ToList();

        return new QuoteDetailResponse(
            quote.PublicId.ToString(),
            quote.QuoteNumber,
            viewer,
            detail.Rfq.PublicId.ToString(),
            detail.Rfq.ReferenceCode,
            detail.Rfq.Title,
            quote.SupplierId.ToString(),
            detail.SupplierName,
            EffectiveStatus(quote, now).ToString(),
            quote.CurrentRevisionNumber,
            quote.ValidUntil,
            quote.DeclineReason,
            quote.AcceptedAt,
            quote.RejectedAt,
            detail.OrderId?.ToString(),
            revisions.First(revision => revision.RevisionNumber == quote.CurrentRevisionNumber),
            revisions,
            detail.Messages
                .OrderBy(message => message.CreatedAt)
                .Select(message => new NegotiationMessageResponse(
                    message.PublicId.ToString(),
                    message.Direction.ToString(),
                    message.Kind.ToString(),
                    message.AgainstRevisionNumber,
                    message.Body,
                    message.CreatedAt))
                .ToList());
    }

    public static QuoteRevisionResponse ToRevision(
        QuoteRevisionDetail detail, Quote quote, IReadOnlyDictionary<Guid, int> lineNumbers)
    {
        QuoteRevision revision = detail.Revision;
        QuoteRevisionStatus status = revision.RevisionNumber != quote.CurrentRevisionNumber
            ? QuoteRevisionStatus.Superseded
            : quote.Status == QuoteStatus.Withdrawn ? QuoteRevisionStatus.Withdrawn : QuoteRevisionStatus.Active;

        return new QuoteRevisionResponse(
            revision.PublicId.ToString(),
            revision.RevisionNumber,
            revision.RevisionKind.ToString(),
            status.ToString(),
            revision.CreatedAt,
            Totals(revision),
            revision.TaxState.ToString(),
            revision.LeadTimeDays,
            revision.ValidUntil,
            revision.PaymentTerms,
            revision.DeliveryTerms,
            revision.PackSizeNote,
            revision.MoqNote,
            revision.TechnicalNotes,
            revision.SupplierNote,
            revision.RequestedChangeRef?.ToString(),
            detail.Items.Select(item => ToItem(item, lineNumbers)).ToList());
    }

    public static QuoteItemResponse ToItem(QuoteRevisionItem item, IReadOnlyDictionary<Guid, int> lineNumbers)
        => new(
            item.RfqItemId?.ToString() ?? string.Empty,
            item.RfqItemId is Guid rfqItemId && lineNumbers.TryGetValue(rfqItemId, out int line) ? line : null,
            item.ProductId?.ToString(),
            item.ProductNameSnapshot,
            item.SkuSnapshot,
            item.Description,
            item.Quantity,
            item.UnitPrice,
            item.LineTotal,
            item.SubstitutionNote);

    public static QuoteTotalsResponse Totals(QuoteRevision revision)
        => new(
            revision.Subtotal,
            revision.DiscountTotal,
            revision.DeliveryCost,
            revision.TaxAmount,
            revision.GrandTotal,
            revision.CurrencyCode);
}
