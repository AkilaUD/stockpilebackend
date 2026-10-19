using Stockpile.Domain.Common;
using Stockpile.Domain.Enums;

namespace Stockpile.Domain.Entities.Procurement;

/// <summary>
/// A supplier's answer to one buying lead (domain-model §6.1). The quote is the
/// identity and the lifecycle; the commercial content is never stored here —
/// every offer is an immutable <see cref="QuoteRevision"/>, so history can never
/// be rewritten (§3.10) and a buyer can always expand the exact terms they were
/// offered.
/// <para>
/// Public id is Guid <b>v4</b> (sensitive): unlike catalogue records, a leaked
/// quote id must not be enumerable across tenants.
/// </para>
/// <para>
/// One quote per (RFQ, supplier) — the unique index is the integrity backstop
/// behind the use case's <c>QUOTE_EXISTS_FOR_RFQ</c> (business-flows §7); a
/// second offer is a revision, never a second row. Status moves only through
/// <c>StateMachine&lt;QuoteStatus&gt;.Evaluate</c> (domain-model §11.3,
/// security-test-matrix X6/S10).
/// </para>
/// </summary>
public sealed class Quote : AggregateRoot
{
    /// <summary>Guid v4 — sensitive (domain-model §6.1).</summary>
    public Guid PublicId { get; set; } = Guid.NewGuid();

    /// <summary>The RFQ being answered.</summary>
    public Guid RfqId { get; set; }

    /// <summary>The lead this quote answers (the supplier's own match row).</summary>
    public Guid RfqMatchId { get; set; }

    /// <summary>Quoting supplier (Supplier aggregate).</summary>
    public Guid SupplierId { get; set; }

    /// <summary>Human-friendly display reference (domain-model §6.1, e.g. <c>QT-2026-1A2B3C4D5</c>); never an authorization input (api.md §1.2).</summary>
    public string QuoteNumber { get; set; } = string.Empty;

    public QuoteStatus Status { get; set; } = QuoteStatus.Draft;

    /// <summary>Pointer to the current revision only; the history lives in <see cref="QuoteRevision"/> rows (§6.1).</summary>
    public int CurrentRevisionNumber { get; set; } = 1;

    /// <summary>Validity carried by the current revision; evaluated again on accept (§3.12 step 3).</summary>
    public DateTime? ValidUntil { get; set; }

    public DateTime? DeclinedAt { get; set; }

    public string? DeclineReason { get; set; }

    public DateTime? AcceptedAt { get; set; }

    public DateTime? RejectedAt { get; set; }

    /// <summary>SQL rowversion for optimistic concurrency on this aggregate root.</summary>
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>
/// One immutable, append-only offer (domain-model §6.2). Nothing here is ever
/// updated in place: a revision is a full snapshot of the commercial and
/// technical terms, so the negotiation timeline can diff
/// <c>RevisionNumber N−1</c> against <c>N</c> without depending on any row that
/// may have changed later (§3.10).
/// <para>
/// Product and supplier names are snapshotted into the revision's
/// <see cref="QuoteRevisionItem"/> rows so a later catalogue edit cannot rewrite
/// quote history — critical for dispute resolution (§6.2).
/// </para>
/// </summary>
public sealed class QuoteRevision
{
    /// <summary>Guid v4 — sensitive (domain-model §6.2).</summary>
    public Guid PublicId { get; set; } = Guid.NewGuid();

    public Guid QuoteId { get; set; }

    /// <summary>1-based; unique per quote (domain-model §6.2).</summary>
    public int RevisionNumber { get; set; }

    /// <summary>Chain to the revision this one supersedes; null only for revision 1.</summary>
    public Guid? PreviousRevisionId { get; set; }

    /// <summary>Acting user's public GUID (a revision is always a person's act).</summary>
    public Guid CreatedBy { get; set; }

    public QuoteRevisionKind RevisionKind { get; set; } = QuoteRevisionKind.Initial;

    /// <summary>The negotiation message that prompted this revision, when there was one. Loose reference: the negotiation subsystem is a later phase, so no FK is declared.</summary>
    public Guid? RequestedChangeRef { get; set; }

    public string CurrencyCode { get; set; } = string.Empty;

    // ---- Server-computed totals (domain-model §6.2, §3.12 step 6). No request
    // carries a total; these are derived from the item lines and nothing else.
    public decimal Subtotal { get; set; }

    public decimal DiscountTotal { get; set; }

    public decimal DeliveryCost { get; set; }

    public decimal TaxAmount { get; set; }

    public decimal GrandTotal { get; set; }

    public TaxState TaxState { get; set; } = TaxState.TaxExcluded;

    public int? LeadTimeDays { get; set; }

    public DateTime? ValidUntil { get; set; }

    public string? PaymentTerms { get; set; }

    public string? DeliveryTerms { get; set; }

    public string? Inclusions { get; set; }

    public string? Exclusions { get; set; }

    public string? PackSizeNote { get; set; }

    public string? MoqNote { get; set; }

    public string? TechnicalNotes { get; set; }

    public string? SupplierNote { get; set; }

    public QuoteRevisionStatus Status { get; set; } = QuoteRevisionStatus.Active;

    /// <summary>When the revision was created (UTC). Append-only: there is deliberately no UpdatedAt (§6.2).</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// One quoted line of a <see cref="QuoteRevision"/> (domain-model §6.2). The
/// description, product name and SKU are snapshots of what was quoted, so the
/// history survives catalogue changes.
/// </summary>
public sealed class QuoteRevisionItem
{
    /// <summary>Guid v4 — sensitive (domain-model §6.2). The revision's identity is the parent; this is the row key.</summary>
    public Guid PublicId { get; set; } = Guid.NewGuid();

    public Guid RevisionId { get; set; }

    /// <summary>The RFQ line this answers. Nullable because the reference allows a line with no RFQ item; the create contract in this phase always names one. The reference is <c>Restrict</c>: the snapshot below is what keeps history readable, and a line a quote answered is never deleted out from under it.</summary>
    public Guid? RfqItemId { get; set; }

    public Guid? ProductId { get; set; }

    /// <summary>Snapshot of the quoted product's name, or the RFQ line's description when the line has no catalogue product.</summary>
    public string ProductNameSnapshot { get; set; } = string.Empty;

    public string? SkuSnapshot { get; set; }

    /// <summary>Snapshot of the buyer's requirement text — never rewritten by the supplier.</summary>
    public string Description { get; set; } = string.Empty;

    public decimal Quantity { get; set; }

    public decimal UnitPrice { get; set; }

    /// <summary>The unit the line is priced in. Non-null per §6.2: when the RFQ line carries no unit, the platform's default unit is used rather than inventing one.</summary>
    public Guid UnitOfMeasureId { get; set; }

    /// <summary>Per-line discount percentage; no create field supplies one in this phase, so it stays null (the column is part of §6.2's tuple).</summary>
    public decimal? LineDiscountPct { get; set; }

    /// <summary>Server-computed: <c>Quantity × UnitPrice</c> less the line discount, rounded once to decimal(18,4).</summary>
    public decimal LineTotal { get; set; }

    public string? SubstitutionNote { get; set; }
}
