using Stockpile.Application.Common.Errors;
using Stockpile.Contracts.Errors;
using Stockpile.Domain.Entities.Subscriptions;
using Stockpile.Domain.Enums;
using Stockpile.Domain.StateMachines;

namespace Stockpile.Application.Subscriptions;

/// <summary>
/// The billing rules both settlement paths share — a verified provider webhook and
/// an operator's manual mark-paid — so a payment has exactly one meaning wherever it
/// arrives. Pure domain logic over already-loaded entities; the callers own the
/// store, the transaction and the audit rows.
/// </summary>
internal static class SubscriptionBilling
{
    private static readonly StateMachine<SubscriptionStatus> Machine = new(TransitionTables.Subscription.All);

    /// <summary>Issues the invoice for the period that starts at <paramref name="periodStart"/>. Amounts come from the plan, never the client; tax is not modelled yet and stays zero.</summary>
    internal static SubscriptionInvoice IssueInvoice(
        SupplierSubscription subscription,
        SubscriptionPlan plan,
        string invoiceNumber,
        DateTime periodStart,
        DateTime now,
        int dueDays)
    {
        decimal amount = plan.PriceAmount
            ?? throw new AppException(ErrorCodes.PlanSalesAssisted);

        return new SubscriptionInvoice
        {
            SubscriptionId = subscription.PublicId,
            InvoiceNumber = invoiceNumber,
            Amount = amount,
            CurrencyCode = plan.CurrencyCode,
            TaxAmount = 0m,
            Total = amount,
            Status = InvoiceStatus.Issued,
            PeriodStart = periodStart,
            PeriodEnd = AddPeriod(periodStart, plan.BillingPeriod),
            IssuedAt = now,
            DueAt = now.AddDays(dueDays),
        };
    }

    /// <summary>
    /// Applies a confirmed payment of <paramref name="invoice"/>: the invoice becomes
    /// <c>Paid</c>, and the subscription's entitlement period covers the invoice's
    /// period. The first payment of a <c>Trial</c> activates it (and an
    /// <c>Expired</c> one renews); a payment on an <c>Active</c> subscription extends it.
    /// Returns false (and changes nothing) when the invoice is not open, so a late or
    /// duplicate settlement can never extend a period twice.
    /// </summary>
    internal static bool Settle(
        SubscriptionInvoice invoice,
        SupplierSubscription subscription,
        DateTime now,
        string? externalRef,
        Guid? actorId,
        string? correlationId)
    {
        if (invoice.Status is not (InvoiceStatus.Issued or InvoiceStatus.Overdue))
        {
            return false;
        }

        invoice.Status = InvoiceStatus.Paid;
        invoice.PaidAt = now;
        invoice.ExternalRef = externalRef;

        DateTime start = invoice.PeriodStart > now ? invoice.PeriodStart : now;
        DateTime paidUntil = start == invoice.PeriodStart ? invoice.PeriodEnd : invoice.PeriodEnd + (start - invoice.PeriodStart);

        string eventType;
        switch (subscription.Status)
        {
            case SubscriptionStatus.Trial:
            case SubscriptionStatus.Expired:
                bool firstActivation = subscription.Status == SubscriptionStatus.Trial;
                subscription.Status = ActivationEdge(subscription.Status);
                subscription.CurrentPeriodStart = start;
                subscription.CurrentPeriodEnd = paidUntil;
                eventType = firstActivation ? "subscription.activated" : "subscription.renewed";
                break;
            case SubscriptionStatus.Active:
            case SubscriptionStatus.PastDue:
            case SubscriptionStatus.GracePeriod:
            case SubscriptionStatus.Suspended:
                // Status recovery for these needs the billing sweep (not built yet);
                // the paid period is recorded so no money is lost track of.
                subscription.CurrentPeriodEnd = paidUntil > subscription.CurrentPeriodEnd
                    ? paidUntil
                    : subscription.CurrentPeriodEnd;
                eventType = "subscription.renewed";
                break;
            case SubscriptionStatus.Cancelled:
                // Paying a cancelled subscription's last invoice changes nothing but the invoice.
                return true;
            default:
                throw new InvalidOperationException($"Unhandled subscription status {subscription.Status}.");
        }

        subscription.UpdatedAt = now;
        subscription.UpdatedBy = actorId;
        subscription.Raise(eventType, actorId, correlationId, new
        {
            subscriptionId = subscription.PublicId,
            supplierId = subscription.SupplierId,
            invoiceId = invoice.PublicId,
            periodEnd = subscription.CurrentPeriodEnd,
        });

        return true;
    }

    /// <summary>Whether the entitlements of <paramref name="subscription"/> apply at <paramref name="now"/>.</summary>
    internal static bool IsEntitled(SupplierSubscription subscription, DateTime now)
    {
        return subscription.Status switch
        {
            SubscriptionStatus.Active or SubscriptionStatus.PastDue or SubscriptionStatus.GracePeriod => true,
            SubscriptionStatus.Trial or SubscriptionStatus.Cancelled => subscription.CurrentPeriodEnd > now,
            SubscriptionStatus.Suspended or SubscriptionStatus.Expired => false,
            _ => throw new InvalidOperationException($"Unhandled subscription status {subscription.Status}."),
        };
    }

    internal static DateTime AddPeriod(DateTime start, BillingPeriod period)
    {
        return period switch
        {
            BillingPeriod.Monthly => start.AddMonths(1),
            BillingPeriod.Quarterly => start.AddMonths(3),
            BillingPeriod.Yearly => start.AddYears(1),
            _ => throw new InvalidOperationException($"Unhandled billing period {period}."),
        };
    }

    private static SubscriptionStatus ActivationEdge(SubscriptionStatus from)
    {
        TransitionResult<SubscriptionStatus> result = Machine.Evaluate(
            from, SubscriptionStatus.Active, ActorContext.System, reason: null);
        return result.IsAllowed
            ? result.Edge!.To
            : throw new AppException(ErrorCodes.StateTransitionInvalid);
    }
}
