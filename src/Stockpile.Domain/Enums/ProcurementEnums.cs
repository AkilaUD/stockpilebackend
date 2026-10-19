namespace Stockpile.Domain.Enums;

/// <summary>
/// Who may see an RFQ (domain-model §5.3). <see cref="MatchedOnly"/> is the
/// default: only the suppliers the matching pipeline selected see the request.
/// </summary>
public enum RfqVisibility
{
    Public = 0,
    MatchedOnly = 1,
}

/// <summary>Who wrote a negotiation message (domain-model §6.3).</summary>
public enum NegotiationDirection
{
    BuyerToSupplier = 0,
    SupplierToBuyer = 1,
}

/// <summary>The typed ask a negotiation message carries (domain-model §6.3, business-flows §8.1).</summary>
public enum NegotiationKind
{
    PriceRequest = 0,
    LeadTimeRequest = 1,
    QuantityClarification = 2,
    AlternativeRequest = 3,
    DeliveryTermsRequest = 4,
    General = 5,
}

/// <summary>
/// Stock hold carried by an order and by each of its lines (domain-model §7.1).
/// <see cref="NotRequired"/> is the honest default: stock is only held for a
/// quoted catalogue line whose product is live-tracked.
/// </summary>
public enum StockReservationState
{
    NotRequired = 0,
    Held = 1,
    Released = 2,
    Consumed = 3,
}

/// <summary>How the RFQ entered the platform (domain-model §5.3) — §3.8 step 1 "entry method".</summary>
public enum RfqSource
{
    Manual = 0,
    ProductSelection = 1,
    DocumentImport = 2,
    ProcurementList = 3,
}
