namespace Stockpile.Contracts.Subscriptions;

/// <summary>One entitlement of a plan (domain-model §3.5): a quota (<c>Limit</c>), an unlimited quota, a tier/descriptive <c>Value</c>, or a plain included feature (none of the three).</summary>
public sealed record EntitlementResponse(string Key, int? Limit, bool Unlimited, string? Value);

/// <summary>A subscription plan as the public list and the admin surface read it. A sales-assisted plan carries no price.</summary>
public sealed record PlanResponse(
    string Id,
    string Code,
    string Name,
    decimal? PriceAmount,
    string Currency,
    string BillingPeriod,
    bool IsSalesAssisted,
    bool IsActive,
    int SortOrder,
    IReadOnlyList<EntitlementResponse> Entitlements);

/// <summary>Start a subscription by plan code (business-flows §12 <c>POST /suppliers/me/subscription</c>).</summary>
public sealed record SubscribeRequest(string PlanCode);

/// <summary>Cancel the supplier's subscription; entitlements run to the end of the paid period.</summary>
public sealed record CancelSubscriptionRequest(string? Reason);

/// <summary>An invoice. Amounts are server-computed from the plan at issue time.</summary>
public sealed record InvoiceResponse(
    string Id,
    string InvoiceNumber,
    string SubscriptionId,
    decimal Amount,
    decimal TaxAmount,
    decimal Total,
    string Currency,
    string Status,
    DateTime PeriodStart,
    DateTime PeriodEnd,
    DateTime IssuedAt,
    DateTime DueAt,
    DateTime? PaidAt,
    string? ExternalRef);

/// <summary>A supplier's subscription with its plan, entitlements and invoices. <c>Entitled</c> is the server's verdict on whether the entitlements apply right now.</summary>
public sealed record SubscriptionResponse(
    string Id,
    PlanResponse Plan,
    string Status,
    DateTime CurrentPeriodStart,
    DateTime CurrentPeriodEnd,
    DateTime? CancelledAt,
    bool AutoRenew,
    bool Entitled,
    IReadOnlyList<InvoiceResponse> Invoices);

/// <summary><c>GET /suppliers/me/subscription</c>: a supplier with no subscription is a normal state, so it is a null member, not an error.</summary>
public sealed record SupplierSubscriptionResponse(SubscriptionResponse? Subscription);

/// <summary>Initiate payment of one invoice (the oldest open one when omitted) through the configured payment provider.</summary>
public sealed record InitiatePaymentRequest(Guid? InvoiceId);

/// <summary>A payment attempt. <c>Instructions</c> is the provider's next step for the payer (the dev file-sink says where it recorded the attempt).</summary>
public sealed record PaymentResponse(
    string Id,
    string InvoiceId,
    string Provider,
    string ProviderRef,
    decimal Amount,
    string Currency,
    string Status,
    DateTime OccurredAt,
    string? Instructions);

/// <summary>A finance operator settling an invoice by hand (bank transfer): a reason is required, a reference is optional.</summary>
public sealed record MarkInvoicePaidRequest(string Reason, string? Reference);

/// <summary>An invoice on the operator's list, with the supplier it bills.</summary>
public sealed record AdminInvoiceResponse(InvoiceResponse Invoice, string SupplierId, string SupplierName);

/// <summary>One entitlement the operator sets on a plan.</summary>
public sealed record EntitlementInput(string Key, int? Limit, bool Unlimited, string? Value);

/// <summary>Create a plan (operator). Prices are configuration, never hard-coded in the UI.</summary>
public sealed record CreatePlanRequest(
    string Code,
    string Name,
    decimal? PriceAmount,
    string Currency,
    string BillingPeriod,
    bool IsSalesAssisted,
    bool IsActive,
    int SortOrder,
    IReadOnlyList<EntitlementInput>? Entitlements);

/// <summary>Replace a plan's editable fields and its whole entitlement set. The code is immutable.</summary>
public sealed record UpdatePlanRequest(
    string Name,
    decimal? PriceAmount,
    string Currency,
    string BillingPeriod,
    bool IsSalesAssisted,
    bool IsActive,
    int SortOrder,
    IReadOnlyList<EntitlementInput>? Entitlements);

/// <summary>The 200 body of a payment webhook; <c>Replayed</c> is true when the delivery had already been processed (one state change, §64).</summary>
public sealed record WebhookAckResponse(bool Replayed);
