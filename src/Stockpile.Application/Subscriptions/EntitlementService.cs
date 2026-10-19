using System.Globalization;
using Stockpile.Application.Common.Errors;
using Stockpile.Contracts.Errors;
using Stockpile.Domain.Entities.Subscriptions;

namespace Stockpile.Application.Subscriptions;

/// <summary>
/// The one place that answers "may this supplier do that?" for plan-gated actions
/// (product uploads, direct contact, …). A gated use case calls
/// <see cref="RequireActiveSubscriptionAsync"/> or <see cref="RequireQuotaAsync"/>
/// instead of re-deriving entitlement rules, so the answer — and the error code the
/// client sees — is identical everywhere:
/// <c>NO_ACTIVE_SUBSCRIPTION</c> (409) when no subscription currently grants
/// entitlements, <c>ENTITLEMENT_LIMIT_REACHED</c> (409) when a quota is used up.
/// <para>
/// No gated endpoint exists yet (product upload and contact intents are later
/// slices), so this helper is built and tested but not wired into a route.
/// </para>
/// </summary>
public sealed class EntitlementService(ISubscriptionsStore store)
{
    /// <summary>Human wording for the quota keys of the reference plans; unknown keys use the key itself.</summary>
    private static readonly Dictionary<string, string> QuotaLabels = new(StringComparer.Ordinal)
    {
        ["directCalls"] = "direct calls",
        ["productUploads"] = "product uploads",
    };

    /// <summary>The supplier's current entitlements, or null when none apply.</summary>
    public async Task<EffectiveEntitlements?> GetAsync(Guid supplierId, CancellationToken cancellationToken)
    {
        SubscriptionRecord? record = await store.FindLatestForSupplierAsync(supplierId, cancellationToken);
        if (record is null || !SubscriptionBilling.IsEntitled(record.Subscription, DateTime.UtcNow))
        {
            return null;
        }

        return new EffectiveEntitlements(record.Plan, [.. record.Plan.Entitlements]);
    }

    /// <summary>Throws <c>NO_ACTIVE_SUBSCRIPTION</c> unless a subscription currently grants entitlements.</summary>
    public async Task<EffectiveEntitlements> RequireActiveSubscriptionAsync(
        Guid supplierId, CancellationToken cancellationToken)
    {
        return await GetAsync(supplierId, cancellationToken)
            ?? throw new AppException(ErrorCodes.NoActiveSubscription);
    }

    /// <summary>
    /// Throws unless the supplier may add one more unit of the quota
    /// <paramref name="entitlementKey"/> given <paramref name="currentUsage"/>.
    /// A sales-assisted plan has no fixed limits; an unlimited entitlement never
    /// runs out; a quota the plan does not list is a limit of zero.
    /// </summary>
    public async Task RequireQuotaAsync(
        Guid supplierId, string entitlementKey, int currentUsage, CancellationToken cancellationToken)
    {
        EffectiveEntitlements effective = await RequireActiveSubscriptionAsync(supplierId, cancellationToken);
        if (effective.Plan.IsSalesAssisted)
        {
            return;
        }

        SubscriptionEntitlement? entitlement = effective.Entitlements.Find(
            candidate => candidate.EntitlementKey == entitlementKey);
        if (entitlement is { IsUnlimited: true })
        {
            return;
        }

        int limit = entitlement?.LimitValue ?? 0;
        if (currentUsage < limit)
        {
            return;
        }

        string label = QuotaLabels.GetValueOrDefault(entitlementKey, entitlementKey);
        throw new AppException(
            ErrorCodes.EntitlementLimitReached,
            string.Create(
                CultureInfo.InvariantCulture,
                $"Your {effective.Plan.Name} plan includes {limit} {label}, and you have used {currentUsage}. Upgrade your plan to continue."));
    }
}

/// <summary>The plan and entitlements that apply to a supplier right now.</summary>
public sealed record EffectiveEntitlements(SubscriptionPlan Plan, List<SubscriptionEntitlement> Entitlements);
