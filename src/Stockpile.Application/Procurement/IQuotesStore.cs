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

    // ---- Lifecycle (P2) -------------------------------------------------------

    /// <summary>
    /// One quote with its history, scoped to the caller's supplier FIRST: another
    /// supplier's quote matches no row, so it is indistinguishable from a missing
    /// one (BOLA, api.md §1.6). <paramref name="forUpdate"/> returns the quote
    /// tracked so the same call's save persists its transition.
    /// </summary>
    Task<QuoteDetail?> LoadForSupplierAsync(
        Guid supplierId, Guid quoteId, bool forUpdate, CancellationToken cancellationToken);

    /// <summary>
    /// One quote with its history, scoped to quotes against RFQs owned by one of
    /// the caller's organisations; any other id is the same null (BOLA).
    /// </summary>
    Task<QuoteDetail?> LoadForBuyerAsync(
        IReadOnlyCollection<Guid> organizationIds, Guid quoteId, bool forUpdate, CancellationToken cancellationToken);

    /// <summary>The supplier's own quotes, newest first, capped at <paramref name="limit"/>.</summary>
    Task<IReadOnlyList<QuoteDetail>> ListForSupplierAsync(
        Guid supplierId, int limit, CancellationToken cancellationToken);

    /// <summary>Quotes against the organisations' RFQs, newest first, capped at <paramref name="limit"/>.</summary>
    Task<IReadOnlyList<QuoteDetail>> ListForBuyerAsync(
        IReadOnlyCollection<Guid> organizationIds, int limit, CancellationToken cancellationToken);

    /// <summary>The RFQ (owned by one of the organisations), its lines and every quote against it; null when the RFQ is not the caller's.</summary>
    Task<RfqQuotes?> LoadRfqQuotesForBuyerAsync(
        IReadOnlyCollection<Guid> organizationIds, Guid rfqId, CancellationToken cancellationToken);

    /// <summary>
    /// Appends a new immutable revision (and its lines, and the supplier's reply
    /// message when there is one) and persists the quote's own transition in ONE
    /// save. No existing revision row is touched. A concurrent revise surfaces as
    /// <c>CONCURRENCY_CONFLICT</c>.
    /// </summary>
    Task AddRevisionAsync(
        Quote quote,
        QuoteRevision revision,
        IReadOnlyCollection<QuoteRevisionItem> items,
        NegotiationMessage? reply,
        CancellationToken cancellationToken);

    /// <summary>Appends a negotiation message and persists the quote's transition together.</summary>
    Task AddMessageAsync(Quote quote, NegotiationMessage message, CancellationToken cancellationToken);

    /// <summary>Persists the tracked changes of a loaded quote; a concurrent edit surfaces as <c>CONCURRENCY_CONFLICT</c>.</summary>
    Task SaveAsync(CancellationToken cancellationToken);
}

/// <summary>One revision with its lines.</summary>
public sealed record QuoteRevisionDetail(QuoteRevision Revision, IReadOnlyList<QuoteRevisionItem> Items);

/// <summary>
/// A quote composed with everything a read needs: its RFQ, the RFQ's lines (with
/// product snapshots), every revision in order, the negotiation thread, the
/// supplier's display identity and the order it produced, when it was accepted.
/// <see cref="Quote"/> is tracked only when loaded for update.
/// </summary>
public sealed record QuoteDetail(
    Quote Quote,
    Rfq Rfq,
    string SupplierName,
    string SupplierSlug,
    IReadOnlyList<QuoteLineContext> Lines,
    IReadOnlyList<QuoteRevisionDetail> Revisions,
    IReadOnlyList<NegotiationMessage> Messages,
    Guid? OrderId)
{
    /// <summary>The revision the quote currently points at.</summary>
    public QuoteRevisionDetail Current =>
        Revisions.First(revision => revision.Revision.RevisionNumber == Quote.CurrentRevisionNumber);
}

/// <summary>An RFQ with its lines and the quotes answering it (comparison input).</summary>
public sealed record RfqQuotes(Rfq Rfq, IReadOnlyList<QuoteLineContext> Lines, IReadOnlyList<QuoteDetail> Quotes);

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
