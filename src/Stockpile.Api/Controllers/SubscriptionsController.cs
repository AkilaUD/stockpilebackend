using System.Security.Claims;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Stockpile.Api.Idempotency;
using Stockpile.Api.RateLimiting;
using Stockpile.Application.Authentication;
using Stockpile.Application.Subscriptions;
using Stockpile.Contracts.Permissions;
using Stockpile.Contracts.Subscriptions;

namespace Stockpile.Api.Controllers;

/// <summary>
/// Subscriptions (api.md §2.10, business-flows §12): the public plan list, a
/// supplier's own subscription, and the operator's billing administration. Prices
/// and entitlements are configuration read from plan rows — nothing here
/// hard-codes a plan. A supplier acts only on its own subscription (a foreign id
/// is the one 404 of api.md §1.6); operator routes require <c>subscription.bill</c>.
/// </summary>
[ApiController]
[ApiVersion(1.0)]
[Route("api/v{version:apiVersion}")]
public sealed class SubscriptionsController(SubscriptionService subscriptions) : ControllerBase
{
    /// <summary>The active plans with their entitlements — public, admin-configured (§3.14).</summary>
    [HttpGet("subscriptions/plans")]
    [AllowAnonymous] // public pricing page (T6.1)
    [EnableRateLimiting(RateLimitPolicies.PublicApi)]
    public async Task<ActionResult<IReadOnlyList<PlanResponse>>> Plans(CancellationToken ct)
        => Ok(await subscriptions.ListPublicPlansAsync(ct));

    /// <summary>The caller's subscription (null member when none).</summary>
    [HttpGet("suppliers/me/subscription")]
    [Authorize(Policy = Permissions.SubscriptionManage)]
    public async Task<ActionResult<SupplierSubscriptionResponse>> GetMine(CancellationToken ct)
        => Ok(await subscriptions.GetMineAsync(SubjectId(), ct));

    /// <summary>Subscribes to a self-serve plan and issues its first invoice; <c>Customized</c> is <c>PLAN_SALES_ASSISTED</c>.</summary>
    [HttpPost("suppliers/me/subscription")]
    [Idempotent]
    [Authorize(Policy = Permissions.SubscriptionManage)]
    [EnableRateLimiting(RateLimitPolicies.SubscriptionWrite)]
    public async Task<ActionResult<SubscriptionResponse>> Subscribe(
        [FromBody] SubscribeRequest request, CancellationToken ct)
    {
        SubscriptionResponse subscription = await subscriptions.SubscribeAsync(SubjectId(), request, ct);
        HttpContext.Items[IdempotencyFilter.ReferenceItemsKey] = subscription.Id;
        return Created("/api/v1/suppliers/me/subscription", subscription);
    }

    /// <summary>Cancels the caller's subscription; paid-up entitlements run to the end of the period.</summary>
    [HttpPost("suppliers/me/subscription/cancel")]
    [Idempotent]
    [Authorize(Policy = Permissions.SubscriptionManage)]
    [EnableRateLimiting(RateLimitPolicies.SubscriptionWrite)]
    public async Task<ActionResult<SubscriptionResponse>> Cancel(
        [FromBody] CancelSubscriptionRequest request, CancellationToken ct)
    {
        SubscriptionResponse subscription = await subscriptions.CancelAsync(SubjectId(), request, ct);
        HttpContext.Items[IdempotencyFilter.ReferenceItemsKey] = subscription.Id;
        return Ok(subscription);
    }

    /// <summary>Starts a payment of one invoice through the configured provider (the dev file-sink here — no live gateway).</summary>
    [HttpPost("subscriptions/{subscriptionId:guid}/payments")]
    [Idempotent]
    [Authorize(Policy = Permissions.SubscriptionManage)]
    [EnableRateLimiting(RateLimitPolicies.SubscriptionWrite)]
    public async Task<ActionResult<PaymentResponse>> InitiatePayment(
        Guid subscriptionId, [FromBody] InitiatePaymentRequest request, CancellationToken ct)
    {
        PaymentResponse payment = await subscriptions.InitiatePaymentAsync(SubjectId(), subscriptionId, request, ct);
        HttpContext.Items[IdempotencyFilter.ReferenceItemsKey] = payment.Id;
        return Created($"/api/v1/subscriptions/{subscriptionId}/payments/{payment.Id}", payment);
    }

    // ---- Operator billing (subscription.bill) ---------------------------------

    /// <summary>Every plan, including inactive ones.</summary>
    [HttpGet("admin/subscription-plans")]
    [Authorize(Policy = Permissions.SubscriptionBill)]
    public async Task<ActionResult<IReadOnlyList<PlanResponse>>> AdminPlans(CancellationToken ct)
        => Ok(await subscriptions.ListAllPlansAsync(ct));

    /// <summary>Creates a plan.</summary>
    [HttpPost("admin/subscription-plans")]
    [Idempotent]
    [Authorize(Policy = Permissions.SubscriptionBill)]
    [EnableRateLimiting(RateLimitPolicies.SubscriptionWrite)]
    public async Task<ActionResult<PlanResponse>> CreatePlan([FromBody] CreatePlanRequest request, CancellationToken ct)
    {
        PlanResponse plan = await subscriptions.CreatePlanAsync(SubjectId(), request, ct);
        HttpContext.Items[IdempotencyFilter.ReferenceItemsKey] = plan.Id;
        return Created($"/api/v1/admin/subscription-plans/{plan.Id}", plan);
    }

    /// <summary>Edits a plan's fields and replaces its entitlement set.</summary>
    [HttpPut("admin/subscription-plans/{planId:guid}")]
    [Authorize(Policy = Permissions.SubscriptionBill)]
    [EnableRateLimiting(RateLimitPolicies.SubscriptionWrite)]
    public async Task<ActionResult<PlanResponse>> UpdatePlan(
        Guid planId, [FromBody] UpdatePlanRequest request, CancellationToken ct)
        => Ok(await subscriptions.UpdatePlanAsync(SubjectId(), planId, request, ct));

    /// <summary>Invoices across suppliers, newest first, optionally filtered by status.</summary>
    [HttpGet("admin/invoices")]
    [Authorize(Policy = Permissions.SubscriptionBill)]
    public async Task<ActionResult<IReadOnlyList<AdminInvoiceResponse>>> AdminInvoices(
        [FromQuery] string? status, CancellationToken ct)
        => Ok(await subscriptions.ListInvoicesAsync(status, ct));

    /// <summary>
    /// A finance operator settles an invoice by hand (bank transfer) with a reason —
    /// the real-world collection path that does not pretend the platform processes
    /// every transaction.
    /// </summary>
    [HttpPost("admin/invoices/{invoiceId:guid}/mark-paid")]
    [Idempotent]
    [Authorize(Policy = Permissions.SubscriptionBill)]
    [EnableRateLimiting(RateLimitPolicies.SubscriptionWrite)]
    public async Task<ActionResult<InvoiceResponse>> MarkPaid(
        Guid invoiceId, [FromBody] MarkInvoicePaidRequest request, CancellationToken ct)
    {
        InvoiceResponse invoice = await subscriptions.MarkInvoicePaidAsync(SubjectId(), invoiceId, request, ct);
        HttpContext.Items[IdempotencyFilter.ReferenceItemsKey] = invoice.Id;
        return Ok(invoice);
    }

    /// <summary>The authenticated subject; [Authorize] guarantees it (1.41).</summary>
    private Guid SubjectId()
        => Guid.Parse(User.FindFirstValue(AuthConstants.SubjectClaim)
            ?? throw new InvalidOperationException(
                "Authorized action without a subject claim; the permission pipeline must have rejected the ticket."));
}
