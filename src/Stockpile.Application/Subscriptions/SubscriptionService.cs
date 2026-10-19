using System.Globalization;
using Microsoft.Extensions.Options;
using Stockpile.Application.Audit;
using Stockpile.Application.Authentication;
using Stockpile.Application.Common.Errors;
using Stockpile.Application.Subscriptions.Payments;
using Stockpile.Application.Suppliers;
using Stockpile.Contracts.Errors;
using Stockpile.Contracts.Permissions;
using Stockpile.Contracts.Subscriptions;
using Stockpile.Domain.Entities.Subscriptions;
using Stockpile.Domain.Enums;
using Stockpile.Domain.StateMachines;

namespace Stockpile.Application.Subscriptions;

/// <summary>
/// Subscriptions: the public plan list, a supplier's subscribe / cancel / pay
/// surface, and the operator's plan and invoice administration
/// (api.md §2.10, business-flows §12).
/// <para>
/// A supplier owns one non-terminal subscription. Subscribing issues the first
/// invoice at the plan's configured price; the subscription is <c>Trial</c> and
/// carries no entitlements until that invoice is paid (no free trial is invented —
/// <c>Subscriptions:TrialDays</c> defaults to 0). Payment is either a provider
/// payment confirmed by a signed webhook (<see cref="PaymentWebhookService"/>) or an
/// operator's manual mark-paid. The platform does not claim to be a payment
/// processor; the only provider here is the dev file-sink.
/// </para>
/// </summary>
public sealed class SubscriptionService(
    ISubscriptionsStore store,
    ISupplierSelfStore suppliers,
    IUserPermissionStore permissions,
    IEnumerable<IPaymentProvider> paymentProviders,
    IOptions<SubscriptionOptions> subscriptionOptions,
    IOptions<PaymentOptions> paymentOptions,
    IAuditService audit,
    ICurrentRequestContext requestContext)
{
    private static readonly StateMachine<SubscriptionStatus> Machine = new(TransitionTables.Subscription.All);

    /// <summary>Cap on the operator's invoice list until cursors land.</summary>
    private const int ListLimit = 100;

    // ---- Public / supplier surface ------------------------------------------

    /// <summary>The active plans, cheapest tier first (public; <c>GET /subscriptions/plans</c>).</summary>
    public async Task<IReadOnlyList<PlanResponse>> ListPublicPlansAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<SubscriptionPlan> plans = await store.ListPlansAsync(activeOnly: true, cancellationToken);
        return [.. plans.Select(SubscriptionMapper.ToPlan)];
    }

    /// <summary>The caller's subscription, or a null member when the supplier has none.</summary>
    public async Task<SupplierSubscriptionResponse> GetMineAsync(Guid userId, CancellationToken cancellationToken)
    {
        Guid supplierId = await RequireSupplierAsync(userId, cancellationToken);
        SubscriptionRecord? record = await store.FindLatestForSupplierAsync(supplierId, cancellationToken);
        return new SupplierSubscriptionResponse(
            record is null ? null : SubscriptionMapper.ToSubscription(record, DateTime.UtcNow));
    }

    /// <summary>Starts a subscription on a self-serve plan and issues its first invoice.</summary>
    public async Task<SubscriptionResponse> SubscribeAsync(
        Guid userId, SubscribeRequest request, CancellationToken cancellationToken)
    {
        Guid supplierId = await RequireSupplierAsync(userId, cancellationToken);

        SubscriptionPlan plan = await store.FindPlanByCodeAsync(request.PlanCode.Trim(), cancellationToken)
            ?? throw new AppException(
                ErrorCodes.ValidationFailed,
                [new FieldError("planCode", "unknown_plan", "Choose one of the available plans.")]);

        if (!plan.IsActive)
        {
            throw new AppException(ErrorCodes.PlanNotActive);
        }

        if (plan.IsSalesAssisted || plan.PriceAmount is null)
        {
            throw new AppException(ErrorCodes.PlanSalesAssisted);
        }

        if (await store.FindOpenForSupplierAsync(supplierId, forUpdate: false, cancellationToken) is not null)
        {
            throw new AppException(ErrorCodes.SubscriptionExists);
        }

        DateTime now = DateTime.UtcNow;
        SupplierSubscription subscription = new()
        {
            SupplierId = supplierId,
            PlanId = plan.PublicId,
            CurrentPeriodStart = now,
            CurrentPeriodEnd = now.AddDays(Math.Max(0, subscriptionOptions.Value.TrialDays)),
            AutoRenew = true,
            CreatedBy = userId,
            CreatedAt = now,
        };

        SubscriptionInvoice invoice = SubscriptionBilling.IssueInvoice(
            subscription,
            plan,
            await NextInvoiceNumberAsync(cancellationToken),
            periodStart: subscription.CurrentPeriodEnd,
            now,
            subscriptionOptions.Value.InvoiceDueDays);

        subscription.Raise("subscription.started", userId, requestContext.CorrelationId, new
        {
            subscriptionId = subscription.PublicId,
            supplierId,
            planCode = plan.Code,
        });

        await store.AddSubscriptionAsync(subscription, invoice, cancellationToken);
        await audit.RecordAsync(
            new AuditEntry("subscription.started", "SupplierSubscription", subscription.PublicId.ToString(), AuditResult.Success),
            cancellationToken);

        return SubscriptionMapper.ToSubscription(new SubscriptionRecord(subscription, plan, [invoice]), now);
    }

    /// <summary>Cancels the caller's open subscription; open invoices are voided and paid-up entitlements run to period end.</summary>
    public async Task<SubscriptionResponse> CancelAsync(
        Guid userId, CancelSubscriptionRequest request, CancellationToken cancellationToken)
    {
        Guid supplierId = await RequireSupplierAsync(userId, cancellationToken);
        SubscriptionRecord record = await store.FindOpenForSupplierAsync(supplierId, forUpdate: true, cancellationToken)
            ?? throw new AppException(ErrorCodes.ResourceNotFound);
        SupplierSubscription subscription = record.Subscription;

        HashSet<string> granted = (await permissions.GetPermissionCodesAsync(userId, cancellationToken))
            .ToHashSet(StringComparer.Ordinal);
        string? reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim();

        TransitionResult<SubscriptionStatus> result = Machine.Evaluate(
            subscription.Status, SubscriptionStatus.Cancelled, ActorContext.ForUser(userId, granted), reason);
        if (!result.IsAllowed)
        {
            throw result.Kind == TransitionResultKind.PermissionDenied
                ? new AppException(ErrorCodes.InsufficientCapability)
                : new AppException(ErrorCodes.StateTransitionInvalid);
        }

        DateTime now = DateTime.UtcNow;
        subscription.Status = result.Edge!.To;
        subscription.CancelledAt = now;
        subscription.CancelReason = reason;
        subscription.AutoRenew = false;
        subscription.UpdatedAt = now;
        subscription.UpdatedBy = userId;

        foreach (SubscriptionInvoice invoice in record.Invoices
                     .Where(invoice => invoice.Status is InvoiceStatus.Draft or InvoiceStatus.Issued or InvoiceStatus.Overdue))
        {
            invoice.Status = InvoiceStatus.Void;
        }

        subscription.Raise("subscription.cancelled", userId, requestContext.CorrelationId, new
        {
            subscriptionId = subscription.PublicId,
            supplierId,
        });

        await store.SaveAsync(cancellationToken);
        await audit.RecordAsync(
            new AuditEntry("subscription.cancelled", "SupplierSubscription", subscription.PublicId.ToString(), AuditResult.Success),
            cancellationToken);

        return SubscriptionMapper.ToSubscription(record, now);
    }

    /// <summary>
    /// Starts a payment of one invoice through the default provider. Re-initiating an
    /// invoice that already has a pending payment returns that payment, so a retry
    /// never makes a second attempt.
    /// </summary>
    public async Task<PaymentResponse> InitiatePaymentAsync(
        Guid userId, Guid subscriptionId, InitiatePaymentRequest request, CancellationToken cancellationToken)
    {
        Guid supplierId = await RequireSupplierAsync(userId, cancellationToken);
        SubscriptionRecord record = await store.FindForSupplierAsync(supplierId, subscriptionId, forUpdate: false, cancellationToken)
            ?? throw new AppException(ErrorCodes.ResourceNotFound);

        if (record.Subscription.Status is SubscriptionStatus.Cancelled or SubscriptionStatus.Expired)
        {
            throw new AppException(ErrorCodes.StateTransitionInvalid);
        }

        IEnumerable<SubscriptionInvoice> open = record.Invoices
            .Where(invoice => invoice.Status is InvoiceStatus.Issued or InvoiceStatus.Overdue);
        SubscriptionInvoice invoice = (request.InvoiceId is { } requested
                ? open.FirstOrDefault(candidate => candidate.PublicId == requested)
                : open.OrderBy(candidate => candidate.IssuedAt).FirstOrDefault())
            ?? throw new AppException(ErrorCodes.StateTransitionInvalid);

        IPaymentProvider provider = ResolveProvider();

        SubscriptionPayment? pending = await store.FindPendingPaymentForInvoiceAsync(invoice.PublicId, cancellationToken);
        if (pending is not null)
        {
            return SubscriptionMapper.ToPayment(pending, invoice.PublicId, instructions: null);
        }

        SubscriptionPayment payment = new()
        {
            InvoiceId = invoice.PublicId,
            Provider = provider.Name,
            Amount = invoice.Total,
            CurrencyCode = invoice.CurrencyCode,
            Status = PaymentStatus.Pending,
            OccurredAt = DateTime.UtcNow,
        };

        PaymentIntent intent = await provider.CreateIntentAsync(
            new PaymentIntentRequest(payment.PublicId, invoice.InvoiceNumber, invoice.Total, invoice.CurrencyCode),
            cancellationToken);
        payment.ProviderRef = intent.ProviderRef;

        await store.AddPaymentAsync(payment, cancellationToken);
        await audit.RecordAsync(
            new AuditEntry("subscription.payment_initiated", "SubscriptionPayment", payment.PublicId.ToString(), AuditResult.Success),
            cancellationToken);

        return SubscriptionMapper.ToPayment(payment, invoice.PublicId, intent.Instructions);
    }

    // ---- Operator surface (subscription.bill) --------------------------------

    /// <summary>Every plan, inactive ones included.</summary>
    public async Task<IReadOnlyList<PlanResponse>> ListAllPlansAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<SubscriptionPlan> plans = await store.ListPlansAsync(activeOnly: false, cancellationToken);
        return [.. plans.Select(SubscriptionMapper.ToPlan)];
    }

    public async Task<PlanResponse> CreatePlanAsync(
        Guid userId, CreatePlanRequest request, CancellationToken cancellationToken)
    {
        string code = request.Code.Trim().ToLowerInvariant();
        if (await store.FindPlanByCodeAsync(code, cancellationToken) is not null)
        {
            throw new AppException(
                ErrorCodes.ValidationFailed,
                [new FieldError("code", "duplicate_code", "A plan with this code already exists.")]);
        }

        SubscriptionPlan plan = new()
        {
            Code = code,
            CreatedBy = userId,
        };
        ApplyPlanFields(
            plan, request.Name, request.PriceAmount, request.Currency, request.BillingPeriod,
            request.IsSalesAssisted, request.IsActive, request.SortOrder, request.Entitlements);

        await store.AddPlanAsync(plan, cancellationToken);
        await audit.RecordAsync(
            new AuditEntry("subscription.plan_created", "SubscriptionPlan", plan.PublicId.ToString(), AuditResult.Success),
            cancellationToken);
        return SubscriptionMapper.ToPlan(plan);
    }

    public async Task<PlanResponse> UpdatePlanAsync(
        Guid userId, Guid planId, UpdatePlanRequest request, CancellationToken cancellationToken)
    {
        SubscriptionPlan plan = await store.FindPlanAsync(planId, forUpdate: true, cancellationToken)
            ?? throw new AppException(ErrorCodes.ResourceNotFound);

        ApplyPlanFields(
            plan, request.Name, request.PriceAmount, request.Currency, request.BillingPeriod,
            request.IsSalesAssisted, request.IsActive, request.SortOrder, request.Entitlements);
        plan.UpdatedAt = DateTime.UtcNow;
        plan.UpdatedBy = userId;

        await store.SaveAsync(cancellationToken);
        await audit.RecordAsync(
            new AuditEntry("subscription.plan_updated", "SubscriptionPlan", plan.PublicId.ToString(), AuditResult.Success),
            cancellationToken);
        return SubscriptionMapper.ToPlan(plan);
    }

    public async Task<IReadOnlyList<AdminInvoiceResponse>> ListInvoicesAsync(
        string? status, CancellationToken cancellationToken)
    {
        InvoiceStatus? filter = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse(status, ignoreCase: true, out InvoiceStatus parsed) || !Enum.IsDefined(parsed))
            {
                throw new AppException(
                    ErrorCodes.ValidationFailed,
                    [new FieldError("status", "invalid_status", "Choose a valid invoice status.")]);
            }

            filter = parsed;
        }

        IReadOnlyList<AdminInvoiceRecord> invoices = await store.ListInvoicesAsync(filter, ListLimit, cancellationToken);
        return
        [
            .. invoices.Select(row => new AdminInvoiceResponse(
                SubscriptionMapper.ToInvoice(row.Invoice),
                row.SupplierPublicId.ToString(),
                row.SupplierName)),
        ];
    }

    /// <summary>
    /// A finance operator settles an invoice by hand (bank transfer). A reason is
    /// mandatory and recorded on the invoice; the settlement is the same one a
    /// verified webhook performs, so there is a single meaning of "paid".
    /// </summary>
    public async Task<InvoiceResponse> MarkInvoicePaidAsync(
        Guid userId, Guid invoiceId, MarkInvoicePaidRequest request, CancellationToken cancellationToken)
    {
        InvoiceRecord record = await store.FindInvoiceAsync(invoiceId, forUpdate: true, cancellationToken)
            ?? throw new AppException(ErrorCodes.ResourceNotFound);
        SubscriptionInvoice invoice = record.Invoice;

        DateTime now = DateTime.UtcNow;
        string? reference = string.IsNullOrWhiteSpace(request.Reference) ? null : request.Reference.Trim();

        if (!SubscriptionBilling.Settle(invoice, record.Subscription, now, reference, userId, requestContext.CorrelationId))
        {
            // Already paid, or void: nothing to settle and nothing to change.
            throw new AppException(ErrorCodes.StateTransitionInvalid);
        }

        invoice.ManualPaymentReason = request.Reason.Trim();
        invoice.MarkedPaidBy = userId;

        await store.AddPaymentAsync(
            new SubscriptionPayment
            {
                InvoiceId = invoice.PublicId,
                Provider = ManualProviderName,
                ProviderRef = $"manual-{invoice.InvoiceNumber}",
                Amount = invoice.Total,
                CurrencyCode = invoice.CurrencyCode,
                Status = PaymentStatus.Succeeded,
                OccurredAt = now,
                SignatureValid = false,
            },
            cancellationToken);

        await audit.RecordAsync(
            new AuditEntry("subscription.invoice_marked_paid", "SubscriptionInvoice", invoice.PublicId.ToString(), AuditResult.Success),
            cancellationToken);

        return SubscriptionMapper.ToInvoice(invoice);
    }

    // ---- Internals ----------------------------------------------------------

    /// <summary>Provider code recorded on a manual (operator) settlement.</summary>
    internal const string ManualProviderName = "manual";

    private async Task<Guid> RequireSupplierAsync(Guid userId, CancellationToken cancellationToken)
    {
        // A caller with no supplier profile is told the resource does not exist — never "you are not a supplier".
        return await suppliers.ResolveSupplierIdAsync(userId, cancellationToken)
            ?? throw new AppException(ErrorCodes.ResourceNotFound);
    }

    private IPaymentProvider ResolveProvider()
    {
        string name = paymentOptions.Value.DefaultProvider;
        IPaymentProvider? provider = paymentProviders.FirstOrDefault(
            candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
        return provider is { IsEnabled: true }
            ? provider
            : throw new AppException(ErrorCodes.PaymentProviderUnavailable);
    }

    private async Task<string> NextInvoiceNumberAsync(CancellationToken cancellationToken)
    {
        // INV-{year}-{9 hex}: unguessable enough that a number does not leak volume, unique-checked
        // here and unique-indexed in the database as the final backstop.
        for (int attempt = 0; attempt < 5; attempt++)
        {
            string number = string.Create(
                CultureInfo.InvariantCulture,
                $"INV-{DateTime.UtcNow.Year}-{Guid.NewGuid().ToString("N")[..9].ToUpperInvariant()}");
            if (!await store.InvoiceNumberExistsAsync(number, cancellationToken))
            {
                return number;
            }
        }

        throw new AppException(ErrorCodes.ConcurrencyConflict);
    }

    private static void ApplyPlanFields(
        SubscriptionPlan plan,
        string name,
        decimal? priceAmount,
        string currency,
        string billingPeriod,
        bool isSalesAssisted,
        bool isActive,
        int sortOrder,
        IReadOnlyList<EntitlementInput>? entitlements)
    {
        if (!isSalesAssisted && priceAmount is null)
        {
            throw new AppException(
                ErrorCodes.ValidationFailed,
                [new FieldError("priceAmount", "price_required", "A self-serve plan needs a price; only a sales-assisted plan may omit it.")]);
        }

        plan.Name = name.Trim();
        plan.PriceAmount = priceAmount;
        plan.CurrencyCode = currency.Trim().ToUpperInvariant();
        plan.BillingPeriod = Enum.Parse<BillingPeriod>(billingPeriod, ignoreCase: true);
        plan.IsSalesAssisted = isSalesAssisted;
        plan.IsActive = isActive;
        plan.SortOrder = sortOrder;

        if (entitlements is null)
        {
            return;
        }

        // Replace the set in place (update / add / remove) — removing and re-adding the same
        // composite key in one save would make EF see two instances with one key.
        HashSet<string> keep = new(entitlements.Select(input => input.Key.Trim()), StringComparer.Ordinal);
        plan.Entitlements.RemoveAll(existing => !keep.Contains(existing.EntitlementKey));

        int order = 0;
        foreach (EntitlementInput input in entitlements)
        {
            string key = input.Key.Trim();
            SubscriptionEntitlement? row = plan.Entitlements.Find(existing => existing.EntitlementKey == key);
            if (row is null)
            {
                row = new SubscriptionEntitlement { PlanId = plan.PublicId, EntitlementKey = key };
                plan.Entitlements.Add(row);
            }

            row.LimitValue = input.Unlimited ? null : input.Limit;
            row.IsUnlimited = input.Unlimited;
            row.TextValue = string.IsNullOrWhiteSpace(input.Value) ? null : input.Value.Trim();
            row.DisplayOrder = order++;
        }
    }
}
