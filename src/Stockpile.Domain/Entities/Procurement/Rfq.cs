using Stockpile.Domain.Common;
using Stockpile.Domain.Enums;

namespace Stockpile.Domain.Entities.Procurement;

/// <summary>
/// Aggregate root (domain-model §5.3): a buyer's request for quote. Owned by an
/// <see cref="Stockpile.Domain.Entities.Identity.Organization"/> — never a user
/// — which is what makes team procurement work. Public id is Guid <b>v4</b>
/// (sensitive): unlike ordinary entities, an RFQ id is guess-resistant so a
/// leaked reference cannot be enumerated across tenants.
/// <para>
/// Status moves only through <c>StateMachine&lt;RFQStatus&gt;.Evaluate</c>
/// (domain-model §11.1, security-test-matrix X6/S10); every change also appends
/// an <see cref="RfqStatusHistory"/> row in the same transaction.
/// </para>
/// </summary>
public sealed class Rfq : AuditableEntity
{
    /// <summary>Guid v4 — sensitive (domain-model §5.3/§6).</summary>
    public Guid PublicId { get; set; } = Guid.NewGuid();

    /// <summary>Buyer owner (Identity).</summary>
    public Guid OrganizationId { get; set; }

    public Guid? ProjectId { get; set; }

    /// <summary>Human-friendly reference, unique, e.g. <c>RFQ-2026-1A2B3C4D</c>.</summary>
    public string ReferenceCode { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public RFQStatus Status { get; set; } = RFQStatus.Draft;

    public Guid? DeliveryAddressId { get; set; }

    public string? DeliveryLocationText { get; set; }

    public DateTime? RequestedDeliveryAt { get; set; }

    public decimal? BudgetAmount { get; set; }

    public string? CurrencyCode { get; set; }

    public bool SubstitutionsAllowed { get; set; }

    public string? SiteConditions { get; set; }

    public string? SpecialInstructions { get; set; }

    public RfqVisibility Visibility { get; set; } = RfqVisibility.MatchedOnly;

    public RfqSource Source { get; set; } = RfqSource.Manual;

    public DateTime? ExpiryAt { get; set; }

    public DateTime? SubmittedAt { get; set; }

    public DateTime? CancelledAt { get; set; }

    public string? CancelReason { get; set; }

    /// <summary>SQL rowversion for optimistic concurrency on this aggregate root.</summary>
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>One line item of an RFQ (domain-model §5.3). Child of <see cref="Rfq"/>.</summary>
public sealed class RfqItem
{
    public Guid PublicId { get; set; } = Guid.NewGuid();

    public Guid RfqId { get; set; }

    /// <summary>1-based position, stable for the buyer's comparison (domain-model §5.3).</summary>
    public int LineNumber { get; set; }

    public Guid? ProductId { get; set; }

    public Guid? CategoryId { get; set; }

    /// <summary>Free-text requirement; the invariant is ≥ 8 characters (domain-model §5.3).</summary>
    public string Description { get; set; } = string.Empty;

    public string? Specification { get; set; }

    public decimal Quantity { get; set; }

    public Guid? UnitOfMeasureId { get; set; }

    public DateTime? ExpectedAt { get; set; }

    public string? Notes { get; set; }
}

/// <summary>
/// A supplier's "buying lead" (domain-model §5.4): one row per supplier the
/// matching pipeline selected for an RFQ. Unique (RfqId, SupplierId).
/// </summary>
public sealed class RfqMatch
{
    public Guid PublicId { get; set; } = Guid.NewGuid();

    public Guid RfqId { get; set; }

    public Guid SupplierId { get; set; }

    public RFQMatchStatus Status { get; set; } = RFQMatchStatus.Matched;

    /// <summary>Transparent rule output (§3.8 step 8) — this slice scores by category overlap.</summary>
    public decimal MatchScore { get; set; }

    /// <summary>Human-readable reasons, e.g. <c>category:plumbing · category:hardware</c>.</summary>
    public string MatchReasons { get; set; } = string.Empty;

    public DateTime? NotifiedAt { get; set; }

    public DateTime? ViewedAt { get; set; }

    public DateTime? RespondedAt { get; set; }

    public string? DeclineReason { get; set; }

    public DateTime? ExpiryAt { get; set; }
}

/// <summary>
/// Immutable append-only status ledger (domain-model §5.5): written inside the
/// same transaction as the change it describes, so the RFQ's history can never
/// disagree with its current status.
/// </summary>
public sealed class RfqStatusHistory
{
    public Guid PublicId { get; set; } = Guid.NewGuid();

    public Guid RfqId { get; set; }

    public RFQStatus FromState { get; set; }

    public RFQStatus ToState { get; set; }

    /// <summary>Acting user's public GUID; null = system (matching pipeline, scheduler).</summary>
    public Guid? ActorId { get; set; }

    public string? Reason { get; set; }

    public string? CorrelationId { get; set; }

    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;
}
