using Stockpile.Contracts.Subscriptions;
using Stockpile.Domain.Entities.Subscriptions;

namespace Stockpile.Application.Subscriptions;

/// <summary>Entity → wire shapes for the subscription surface. Internal ids never leave; every id is a public one.</summary>
internal static class SubscriptionMapper
{
    internal static PlanResponse ToPlan(SubscriptionPlan plan)
    {
        return new PlanResponse(
            plan.PublicId.ToString(),
            plan.Code,
            plan.Name,
            plan.PriceAmount,
            plan.CurrencyCode,
            plan.BillingPeriod.ToString(),
            plan.IsSalesAssisted,
            plan.IsActive,
            plan.SortOrder,
            [.. plan.Entitlements
                .OrderBy(entitlement => entitlement.DisplayOrder)
                .ThenBy(entitlement => entitlement.EntitlementKey, StringComparer.Ordinal)
                .Select(entitlement => new EntitlementResponse(
                    entitlement.EntitlementKey,
                    entitlement.LimitValue,
                    entitlement.IsUnlimited,
                    entitlement.TextValue))]);
    }

    internal static InvoiceResponse ToInvoice(SubscriptionInvoice invoice)
    {
        return new InvoiceResponse(
            invoice.PublicId.ToString(),
            invoice.InvoiceNumber,
            invoice.SubscriptionId.ToString(),
            invoice.Amount,
            invoice.TaxAmount,
            invoice.Total,
            invoice.CurrencyCode,
            invoice.Status.ToString(),
            invoice.PeriodStart,
            invoice.PeriodEnd,
            invoice.IssuedAt,
            invoice.DueAt,
            invoice.PaidAt,
            invoice.ExternalRef);
    }

    internal static SubscriptionResponse ToSubscription(SubscriptionRecord record, DateTime now)
    {
        SupplierSubscription subscription = record.Subscription;
        return new SubscriptionResponse(
            subscription.PublicId.ToString(),
            ToPlan(record.Plan),
            subscription.Status.ToString(),
            subscription.CurrentPeriodStart,
            subscription.CurrentPeriodEnd,
            subscription.CancelledAt,
            subscription.AutoRenew,
            SubscriptionBilling.IsEntitled(subscription, now),
            [.. record.Invoices.OrderByDescending(invoice => invoice.IssuedAt).Select(ToInvoice)]);
    }

    internal static PaymentResponse ToPayment(SubscriptionPayment payment, Guid invoiceId, string? instructions)
    {
        return new PaymentResponse(
            payment.PublicId.ToString(),
            invoiceId.ToString(),
            payment.Provider,
            payment.ProviderRef,
            payment.Amount,
            payment.CurrencyCode,
            payment.Status.ToString(),
            payment.OccurredAt,
            instructions);
    }
}
