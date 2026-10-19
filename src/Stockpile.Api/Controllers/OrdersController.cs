using System.Security.Claims;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Stockpile.Api.Idempotency;
using Stockpile.Api.RateLimiting;
using Stockpile.Application.Authentication;
using Stockpile.Application.Orders;
using Stockpile.Contracts.Orders;
using Stockpile.Contracts.Permissions;

namespace Stockpile.Api.Controllers;

/// <summary>
/// Orders (api.md §2.8, business-flows §10). An order is created by accepting a
/// quote (<c>POST /quotes/{id}/accept</c>); this controller is its read surface and
/// the supplier's acknowledgement. Both parties see only their own side: the buyer
/// the orders of its organisations, the supplier the orders placed with it — any
/// other id is the one 404 of api.md §1.6. Payment and delivery happen outside the
/// marketplace, so the order is a record and fulfilment tracker, not a payment.
/// </summary>
[ApiController]
[ApiVersion(1.0)]
[Route("api/v{version:apiVersion}")]
public sealed class OrdersController(OrderService orders) : ControllerBase
{
    /// <summary>The orders placed by the caller's organisations, newest first (<c>order.read.own</c>).</summary>
    [HttpGet("me/orders")]
    [Authorize(Policy = Permissions.OrderReadOwn)]
    public async Task<ActionResult<IReadOnlyList<OrderSummaryResponse>>> ListMine(CancellationToken ct)
        => Ok(await orders.ListForBuyerAsync(SubjectId(), ct));

    /// <summary>The orders placed with the caller's supplier, newest first (<c>order.fulfill</c>).</summary>
    [HttpGet("suppliers/me/orders")]
    [Authorize(Policy = Permissions.OrderFulfill)]
    public async Task<ActionResult<IReadOnlyList<OrderSummaryResponse>>> ListForSupplier(CancellationToken ct)
        => Ok(await orders.ListForSupplierAsync(SubjectId(), ct));

    /// <summary>One order with its lines and status history, as its buyer or its supplier.</summary>
    [HttpGet("orders/{orderId:guid}")]
    [Authorize]
    public async Task<ActionResult<OrderDetailResponse>> Get(Guid orderId, CancellationToken ct)
        => Ok(await orders.GetAsync(SubjectId(), orderId, ct));

    /// <summary>The supplier acknowledges a confirmed order (<c>order.fulfill</c>).</summary>
    [HttpPost("orders/{orderId:guid}/acknowledge")]
    [Idempotent]
    [Authorize(Policy = Permissions.OrderFulfill)]
    [EnableRateLimiting(RateLimitPolicies.OrderWrite)]
    public async Task<ActionResult<OrderDetailResponse>> Acknowledge(Guid orderId, CancellationToken ct)
    {
        OrderDetailResponse order = await orders.AcknowledgeAsync(SubjectId(), orderId, ct);
        HttpContext.Items[IdempotencyFilter.ReferenceItemsKey] = order.PublicId;
        return Ok(order);
    }

    /// <summary>The authenticated subject; [Authorize] guarantees it (1.41).</summary>
    private Guid SubjectId()
        => Guid.Parse(User.FindFirstValue(AuthConstants.SubjectClaim)
            ?? throw new InvalidOperationException(
                "Authorized action without a subject claim; the permission pipeline must have rejected the ticket."));
}
