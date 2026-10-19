using Stockpile.Domain.Entities.Procurement;

namespace Stockpile.Application.Procurement;

/// <summary>
/// Quote data port (architecture §8.5 module "procurement"). Writes stay
/// transaction-shaped on the store side; the Application layer owns the rules
/// (coverage, totals, transitions). Reads return composed details — including the
/// catalogue snapshots a revision needs — so the service never re-joins reference
/// data and never trusts a client-supplied total.
/// </summary>
public interface IQuotesStore
{
    /// <summary>True when a quote already carries this display reference (domain-model §6.1 unique).</summary>
    Task<bool> QuoteNumberExistsAsync(string quoteNumber, CancellationToken cancellationToken);

    /// <summary>
    /// Everything the submit use case needs, scoped to the caller's supplier:
    /// the lead, the RFQ it answers, the RFQ's lines with their product snapshots,
    /// and any quote this supplier already holds for that RFQ (§6.1 unique
    /// (RfqId, SupplierId) — the reason a second offer is <c>QUOTE_EXISTS_FOR_RFQ</c>
    /// and not a second row).
    /// <para>
    /// A lead that is not the caller's supplier's matches nothing and returns
    /// null, so another supplier's lead is never inferable (BOLA, api.md §1.6).
    /// The returned entities stay tracked: the same call's save persists the lead's
    /// own transition with the quote.
    /// </para>
    /// </summary>
    Task<QuoteSubmission?> LoadSubmissionAsync(
        Guid supplierId, Guid rfqMatchId, CancellationToken cancellationToken);

    /// <summary>
    /// Persists the quote with its first revision and lines, and the lead's own
    /// status change, in ONE <c>SaveChanges</c> — the quote and the "I answered
    /// this" fact commit together or not at all.
    /// </summary>
    Task AddSubmissionAsync(
        Quote quote,
        QuoteRevision revision,
        IReadOnlyCollection<QuoteRevisionItem> items,
        RfqMatch match,
        CancellationToken cancellationToken);
}

/// <summary>The composed facts of one quote submission (see <see cref="IQuotesStore.LoadSubmissionAsync"/>).</summary>
public sealed record QuoteSubmission(
    RfqMatch Match,
    Rfq Rfq,
    IReadOnlyList<QuoteLineContext> Lines,
    Quote? ExistingQuote);

/// <summary>
/// One RFQ line the supplier may price, with the catalogue snapshot to store:
/// the product name when the line names a product, otherwise the buyer's own
/// requirement text (domain-model §6.2 — names are snapshotted, never re-joined
/// at read time).
/// </summary>
public sealed record QuoteLineContext(RfqItem Item, string? ProductName, string? ProductSku);
