namespace Stockpile.Domain.Enums;

/// <summary>How often a plan bills (domain-model §3.5).</summary>
public enum BillingPeriod
{
    Monthly = 0,
    Quarterly = 1,
    Yearly = 2,
}

/// <summary>Invoice lifecycle (domain-model §3.5).</summary>
public enum InvoiceStatus
{
    Draft = 0,
    Issued = 1,
    Paid = 2,
    Void = 3,
    Overdue = 4,
}

/// <summary>Outcome of one payment attempt (domain-model §3.5 <c>SubscriptionPayment</c>).</summary>
public enum PaymentStatus
{
    Pending = 0,
    Succeeded = 1,
    Failed = 2,
}
