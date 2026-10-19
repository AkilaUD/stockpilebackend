namespace Stockpile.Contracts.Orders;

/// <summary>
/// Response of <c>POST /quotes/{id}/accept</c> (business-flows §10): the order
/// the acceptance created. A replay of the same <c>Idempotency-Key</c> returns the
/// original order id (the stored reference), never a second order.
/// </summary>
public sealed record AcceptQuoteResponse(
    string OrderId,
    string OrderNumber,
    string Status,
    string StockReservationState);

/// <summary>One row of an order list (buyer's <c>/me/orders</c> or supplier's <c>/suppliers/me/orders</c>).</summary>
public sealed record OrderSummaryResponse(
    string PublicId,
    string OrderNumber,
    string QuoteId,
    string RfqId,
    string SupplierId,
    string SupplierName,
    string BuyerName,
    string Status,
    decimal GrandTotal,
    string Currency,
    string StockReservationState,
    DateTime CreatedAt);

/// <summary>One ordered line — a snapshot of the accepted revision's line.</summary>
public sealed record OrderItemResponse(
    int LineNumber,
    string? ProductId,
    string ProductName,
    string? Sku,
    string Description,
    decimal Quantity,
    decimal UnitPrice,
    decimal LineTotal,
    string StockState);

/// <summary>One entry of the order's append-only status ledger.</summary>
public sealed record OrderStatusEntryResponse(string From, string To, DateTime OccurredAt, string? Reason);

/// <summary>An order with its lines and status history. Every figure is the accepted revision's, never recomputed from the live quote.</summary>
public sealed record OrderDetailResponse(
    string PublicId,
    string OrderNumber,
    string QuoteId,
    string QuoteNumber,
    int QuoteRevisionNumber,
    string RfqId,
    string SupplierId,
    string SupplierName,
    string BuyerName,
    string Status,
    string Currency,
    decimal Subtotal,
    decimal DiscountTotal,
    decimal DeliveryCost,
    decimal TaxAmount,
    decimal GrandTotal,
    int? LeadTimeDays,
    string? PaymentTerms,
    string? DeliveryTerms,
    string? DeliveryLocationText,
    DateTime? RequestedDeliveryAt,
    string StockReservationState,
    DateTime CreatedAt,
    DateTime? ConfirmedAt,
    DateTime? AcknowledgedAt,
    IReadOnlyList<OrderItemResponse> Items,
    IReadOnlyList<OrderStatusEntryResponse> History);
