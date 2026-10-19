using Stockpile.Domain.Entities.Subscriptions;
using Stockpile.Domain.Enums;

namespace Stockpile.Application.Subscriptions;

/// <summary>
/// Persistence port for plans, subscriptions, invoices and payments. Loaders return
/// tracked entities when <c>forUpdate</c> is set; mutations are staged on them and
/// committed by <see cref="SaveAsync"/>, which maps database failures to
/// <c>AppException</c> codes (the store owns that translation, as elsewhere).
/// </summary>
public interface ISubscriptionsStore
{
    /// <summary>Plans with their entitlements, ordered by <c>SortOrder</c>.</summary>
    Task<IReadOnlyList<SubscriptionPlan>> ListPlansAsync(bool activeOnly, CancellationToken cancellationToken);

    Task<SubscriptionPlan?> FindPlanByCodeAsync(string code, CancellationToken cancellationToken);

    /// <summary>A plan with its entitlements by public id; tracked when <paramref name="forUpdate"/>.</summary>
    Task<SubscriptionPlan?> FindPlanAsync(Guid planPublicId, bool forUpdate, CancellationToken cancellationToken);

    Task AddPlanAsync(SubscriptionPlan plan, CancellationToken cancellationToken);

    /// <summary>The supplier's non-terminal subscription (at most one — a filtered unique index guarantees it).</summary>
    Task<SubscriptionRecord?> FindOpenForSupplierAsync(Guid supplierId, bool forUpdate, CancellationToken cancellationToken);

    /// <summary>The supplier's most recent subscription whatever its status (so a cancelled-but-paid-up one still shows).</summary>
    Task<SubscriptionRecord?> FindLatestForSupplierAsync(Guid supplierId, CancellationToken cancellationToken);

    /// <summary>A subscription by public id, only if it belongs to <paramref name="supplierId"/> (BOLA: foreign and missing look the same).</summary>
    Task<SubscriptionRecord?> FindForSupplierAsync(
        Guid supplierId, Guid subscriptionPublicId, bool forUpdate, CancellationToken cancellationToken);

    Task<bool> InvoiceNumberExistsAsync(string invoiceNumber, CancellationToken cancellationToken);

    /// <summary>Adds a subscription with its first invoice. A second open subscription for the supplier surfaces as <c>SUBSCRIPTION_EXISTS</c>.</summary>
    Task AddSubscriptionAsync(SupplierSubscription subscription, SubscriptionInvoice invoice, CancellationToken cancellationToken);

    Task AddPaymentAsync(SubscriptionPayment payment, CancellationToken cancellationToken);

    /// <summary>An invoice by public id with its subscription and plan (tracked when <paramref name="forUpdate"/>).</summary>
    Task<InvoiceRecord?> FindInvoiceAsync(Guid invoicePublicId, bool forUpdate, CancellationToken cancellationToken);

    Task<IReadOnlyList<AdminInvoiceRecord>> ListInvoicesAsync(
        InvoiceStatus? status, int limit, CancellationToken cancellationToken);

    /// <summary>The still-pending provider payment for an invoice, if one was already initiated.</summary>
    Task<SubscriptionPayment?> FindPendingPaymentForInvoiceAsync(Guid invoiceId, CancellationToken cancellationToken);

    /// <summary>A payment by (provider, providerRef) with its invoice chain, tracked — the webhook settles it in place.</summary>
    Task<PaymentRecord?> FindPaymentForUpdateAsync(string provider, string providerRef, CancellationToken cancellationToken);

    Task SaveAsync(CancellationToken cancellationToken);
}

/// <summary>A subscription with its plan (entitlements loaded) and invoices, newest first.</summary>
public sealed record SubscriptionRecord(
    SupplierSubscription Subscription, SubscriptionPlan Plan, IReadOnlyList<SubscriptionInvoice> Invoices);

/// <summary>An invoice with the subscription and plan it bills.</summary>
public sealed record InvoiceRecord(
    SubscriptionInvoice Invoice, SupplierSubscription Subscription, SubscriptionPlan Plan);

/// <summary>An invoice with the supplier it bills, for the operator's list.</summary>
public sealed record AdminInvoiceRecord(
    SubscriptionInvoice Invoice, SupplierSubscription Subscription, Guid SupplierPublicId, string SupplierName);

/// <summary>A payment with the invoice chain it settles.</summary>
public sealed record PaymentRecord(SubscriptionPayment Payment, InvoiceRecord Invoice);
