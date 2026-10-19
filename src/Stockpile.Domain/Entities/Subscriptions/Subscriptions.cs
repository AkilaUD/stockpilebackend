using Stockpile.Domain.Common;
using Stockpile.Domain.Enums;

namespace Stockpile.Domain.Entities.Subscriptions;

/// <summary>
/// A subscription plan (domain-model §3.5, §3.14). Plans are admin-configurable
/// configuration rows — never hard-coded in the UI — so the seeder only inserts
/// a missing plan and never overwrites an operator's edit.
/// <para>Public id is Guid <b>v7</b>: plans are non-sensitive catalogue-style rows.</para>
/// </summary>
public sealed class SubscriptionPlan : AuditableEntity
{
    /// <summary>Guid v7 — non-sensitive.</summary>
    public Guid PublicId { get; set; } = Guid.CreateVersion7();

    /// <summary>Stable unique code (<c>starter</c>, <c>standard</c>, …) — what a supplier subscribes by.</summary>
    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>Null for a sales-assisted plan with no fixed price.</summary>
    public decimal? PriceAmount { get; set; }

    public string CurrencyCode { get; set; } = string.Empty;

    public BillingPeriod BillingPeriod { get; set; } = BillingPeriod.Monthly;

    /// <summary>True for the Customized plan: arranged with sales, never self-served.</summary>
    public bool IsSalesAssisted { get; set; }

    /// <summary>Inactive plans are hidden from the public list and cannot be subscribed to.</summary>
    public bool IsActive { get; set; } = true;

    public int SortOrder { get; set; }

    /// <summary>SQL rowversion for optimistic concurrency on this aggregate root.</summary>
    public byte[] RowVersion { get; set; } = [];

    /// <summary>Entitlement rows of the plan (children; replaced as a set by the admin editor).</summary>
    public List<SubscriptionEntitlement> Entitlements { get; } = [];
}

/// <summary>
/// One entitlement a plan grants (domain-model §3.5). A numeric quota carries
/// <see cref="LimitValue"/>; <see cref="IsUnlimited"/> lifts it; a tier or
/// descriptive value (<c>basic</c>, <c>Mon–Fri</c>, <c>none</c>) rides
/// <see cref="TextValue"/>; a plain included feature has neither.
/// </summary>
public sealed class SubscriptionEntitlement
{
    public Guid PlanId { get; set; }

    /// <summary>e.g. <c>directCalls</c>, <c>productUploads</c>, <c>transactionFees</c>; part of the composite key.</summary>
    public string EntitlementKey { get; set; } = string.Empty;

    public int? LimitValue { get; set; }

    public bool IsUnlimited { get; set; }

    public string? TextValue { get; set; }

    public int DisplayOrder { get; set; }
}

/// <summary>
/// A supplier's subscription (domain-model §3.5). At most one non-terminal row
/// per supplier — enforced by a filtered unique index, the integrity backstop
/// behind <c>SUBSCRIPTION_EXISTS</c>. Status moves only through
/// <c>StateMachine&lt;SubscriptionStatus&gt;</c> (domain-model §11.7); billing
/// edges are payment- or scheduler-driven.
/// <para>Public id is Guid <b>v4</b> (sensitive: it names a supplier's billing relationship).</para>
/// </summary>
public sealed class SupplierSubscription : AggregateRoot
{
    /// <summary>Guid v4 — sensitive.</summary>
    public Guid PublicId { get; set; } = Guid.NewGuid();

    public Guid SupplierId { get; set; }

    public Guid PlanId { get; set; }

    public SubscriptionStatus Status { get; set; } = SubscriptionStatus.Trial;

    public DateTime CurrentPeriodStart { get; set; }

    public DateTime CurrentPeriodEnd { get; set; }

    public DateTime? GraceEndsAt { get; set; }

    public DateTime? CancelledAt { get; set; }

    public string? CancelReason { get; set; }

    public bool AutoRenew { get; set; } = true;

    /// <summary>SQL rowversion for optimistic concurrency on this aggregate root.</summary>
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>
/// A bill for one subscription period (domain-model §3.5). Amounts are
/// server-computed from the plan at issue time and never edited afterwards.
/// </summary>
public sealed class SubscriptionInvoice
{
    /// <summary>Guid v4 — sensitive.</summary>
    public Guid PublicId { get; set; } = Guid.NewGuid();

    public Guid SubscriptionId { get; set; }

    /// <summary>Unique display reference (e.g. <c>INV-2026-1A2B3C4D5</c>).</summary>
    public string InvoiceNumber { get; set; } = string.Empty;

    public decimal Amount { get; set; }

    public string CurrencyCode { get; set; } = string.Empty;

    public decimal TaxAmount { get; set; }

    public decimal Total { get; set; }

    public InvoiceStatus Status { get; set; } = InvoiceStatus.Issued;

    public DateTime PeriodStart { get; set; }

    public DateTime PeriodEnd { get; set; }

    public DateTime IssuedAt { get; set; } = DateTime.UtcNow;

    public DateTime DueAt { get; set; }

    public DateTime? PaidAt { get; set; }

    /// <summary>Reference to the external settlement (provider ref or bank transfer slip), when paid.</summary>
    public string? ExternalRef { get; set; }

    /// <summary>Operator-supplied reason for a manual mark-paid; null for a provider settlement.</summary>
    public string? ManualPaymentReason { get; set; }

    /// <summary>Operator who marked the invoice paid by hand; null otherwise.</summary>
    public Guid? MarkedPaidBy { get; set; }

    /// <summary>SQL rowversion — two settlements racing for one invoice must not both win.</summary>
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>
/// One payment attempt or settlement against an invoice (domain-model §3.5).
/// Webhook-driven rows are idempotent on (<see cref="Provider"/>,
/// <see cref="ProviderRef"/>) — a replayed webhook produces ONE state change.
/// </summary>
public sealed class SubscriptionPayment
{
    public Guid PublicId { get; set; } = Guid.NewGuid();

    public Guid InvoiceId { get; set; }

    /// <summary>Provider name (<c>filesink</c>, <c>manual</c>, or a real gateway code).</summary>
    public string Provider { get; set; } = string.Empty;

    public string ProviderRef { get; set; } = string.Empty;

    public decimal Amount { get; set; }

    public string CurrencyCode { get; set; } = string.Empty;

    public PaymentStatus Status { get; set; } = PaymentStatus.Pending;

    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;

    /// <summary>True only when a webhook's signature verified; manual and initiated rows are false.</summary>
    public bool SignatureValid { get; set; }

    /// <summary>The <c>Idempotency-Key</c> of the delivery that settled this row; null until settled.</summary>
    public string? IdempotencyKey { get; set; }

    /// <summary>Verified webhook body, kept for dispute and reconciliation. Never logged.</summary>
    public string? RawPayload { get; set; }

    /// <summary>SQL rowversion — two deliveries racing to settle one payment must not both win.</summary>
    public byte[] RowVersion { get; set; } = [];
}
