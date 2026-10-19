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

/// <summary>How the RFQ entered the platform (domain-model §5.3) — §3.8 step 1 "entry method".</summary>
public enum RfqSource
{
    Manual = 0,
    ProductSelection = 1,
    DocumentImport = 2,
    ProcurementList = 3,
}
