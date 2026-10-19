using Stockpile.Domain.Common;
using Stockpile.Domain.Enums;

namespace Stockpile.Domain.Entities.Orders;

/// <summary>
/// The binding order created when a buyer accepts a quote (domain-model §7.1,
/// business-flows §3.12). Every commercial fact is a snapshot of the accepted
/// revision, so the order stays readable and defensible even if the catalogue,
/// the quote's later history or the supplier profile change.
/// <para>
/// Public id is Guid <b>v4</b> (sensitive). One order per quote — the unique
/// <c>QuoteId</c> index is the integrity backstop that makes a concurrent or
/// repeated accept produce exactly one order. Status moves only through
/// <c>StateMachine&lt;PurchaseOrderStatus&gt;.Evaluate</c> (domain-model §11.4).
/// </para>
/// </summary>
public sealed class PurchaseOrder : AggregateRoot
{
    /// <summary>Guid v4 — sensitive.</summary>
    public Guid PublicId { get; set; } = Guid.NewGuid();

    /// <summary>Human-friendly display reference (e.g. <c>PO-2026-1A2B3C4D5</c>); never an authorization input.</summary>
    public string OrderNumber { get; set; } = string.Empty;

    public Guid RfqId { get; set; }

    /// <summary>The accepted quote — unique: one order per quote.</summary>
    public Guid QuoteId { get; set; }

    /// <summary>The exact accepted revision (an immutable snapshot).</summary>
    public Guid QuoteRevisionId { get; set; }

    public Guid SupplierId { get; set; }

    /// <summary>Buyer owner (Identity organisation).</summary>
    public Guid BuyerOrganizationId { get; set; }

    public PurchaseOrderStatus Status { get; set; } = PurchaseOrderStatus.Draft;

    public string CurrencyCode { get; set; } = string.Empty;

    public decimal Subtotal { get; set; }

    public decimal DiscountTotal { get; set; }

    public decimal DeliveryCost { get; set; }

    public decimal TaxAmount { get; set; }

    public decimal GrandTotal { get; set; }

    public int? LeadTimeDays { get; set; }

    public string? PaymentTerms { get; set; }

    public string? DeliveryTerms { get; set; }

    /// <summary>Delivery location text copied from the RFQ at order time; later RFQ edits cannot move an order.</summary>
    public string? DeliveryLocationText { get; set; }

    public DateTime? RequestedDeliveryAt { get; set; }

    /// <summary>Whether any line carries a stock hold (domain-model §7.1).</summary>
    public StockReservationState StockReservation { get; set; } = StockReservationState.NotRequired;

    public DateTime? ConfirmedAt { get; set; }

    public DateTime? AcknowledgedAt { get; set; }

    public DateTime? CancelledAt { get; set; }

    public string? CancelReason { get; set; }

    /// <summary>SQL rowversion for optimistic concurrency on this aggregate root.</summary>
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>One ordered line — a snapshot of one accepted <c>QuoteRevisionItem</c> (domain-model §7.2).</summary>
public sealed class PurchaseOrderItem
{
    public Guid PublicId { get; set; } = Guid.NewGuid();

    public Guid PurchaseOrderId { get; set; }

    public int LineNumber { get; set; }

    public Guid? ProductId { get; set; }

    public string ProductNameSnapshot { get; set; } = string.Empty;

    public string? SkuSnapshot { get; set; }

    public string Description { get; set; } = string.Empty;

    public decimal Quantity { get; set; }

    public Guid UnitOfMeasureId { get; set; }

    public decimal UnitPrice { get; set; }

    public decimal? LineDiscountPct { get; set; }

    public decimal LineTotal { get; set; }

    /// <summary>Hold taken on tracked stock for this line; <see cref="StockReservationState.NotRequired"/> when none.</summary>
    public StockReservationState StockState { get; set; } = StockReservationState.NotRequired;
}

/// <summary>Immutable append-only order status ledger, written in the same transaction as the change (domain-model §7.3).</summary>
public sealed class PurchaseOrderStatusHistory
{
    public Guid PublicId { get; set; } = Guid.NewGuid();

    public Guid PurchaseOrderId { get; set; }

    public PurchaseOrderStatus FromState { get; set; }

    public PurchaseOrderStatus ToState { get; set; }

    public Guid? ActorId { get; set; }

    public string? Reason { get; set; }

    public string? CorrelationId { get; set; }

    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;
}
