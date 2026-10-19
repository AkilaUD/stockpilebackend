using System.Security.Claims;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Stockpile.Api.RateLimiting;
using Stockpile.Application.Authentication;
using Stockpile.Application.Suppliers;
using Stockpile.Contracts.Permissions;

namespace Stockpile.Api.Controllers;

/// <summary>
/// The signed-in supplier's buying-lead inbox (business-flows §6, api.md §2.9):
/// the matched RFQs dispatched to this supplier, the lead view, and the two
/// actions the supplier can take — recording an open and declining with a
/// reason. The caller's supplier comes from their own accepted organisation
/// membership, so every route answers exactly that supplier's leads and a
/// foreign or unknown lead id is one 404 (BOLA, api.md §1.6). Declining carries
/// the §11.2 capability <c>rfq.match.decline</c>, which both supplier roles
/// hold; the reads and the open are open to any member of the supplier.
/// </summary>
[ApiController]
[ApiVersion(1.0)]
[Route("api/v{version:apiVersion}/suppliers/me/leads")]
[Authorize]
public sealed class SupplierLeadsController(SupplierLeadsService service) : ControllerBase
{
    /// <summary>The supplier's leads, newest first, cursor-paginated (api.md §1).</summary>
    [HttpGet]
    public async Task<ActionResult<SupplierLeadPageResponse>> List(
        [FromQuery] string? status,
        [FromQuery] string? cursor,
        [FromQuery] int? limit,
        CancellationToken ct)
        => Ok(await service.ListAsync(SubjectId(), status, cursor, limit, ct));

    /// <summary>One lead as business-flows §6 permits the supplier to see it.</summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<SupplierLeadDetailResponse>> Get(Guid id, CancellationToken ct)
        => Ok(await service.GetAsync(SubjectId(), id, ct));

    /// <summary>Records that the supplier opened the lead (§11.2 <c>Matched → Notified → Viewed</c>).</summary>
    [HttpPost("{id:guid}/view")]
    [EnableRateLimiting(RateLimitPolicies.SupplierLeadWrite)]
    public async Task<ActionResult<SupplierLeadDetailResponse>> View(Guid id, CancellationToken ct)
        => Ok(await service.ViewAsync(SubjectId(), id, ct));

    /// <summary>Declines the lead; §11.2 requires the reason (api.md §2.9).</summary>
    [HttpPost("{id:guid}/decline")]
    [Authorize(Policy = Permissions.RfqMatchDecline)]
    [EnableRateLimiting(RateLimitPolicies.SupplierLeadWrite)]
    public async Task<ActionResult<SupplierLeadDetailResponse>> Decline(
        Guid id, [FromBody] DeclineSupplierLeadRequest request, CancellationToken ct)
        => Ok(await service.DeclineAsync(SubjectId(), id, request, ct));

    /// <summary>The authenticated subject; [Authorize] guarantees it (1.41).</summary>
    private Guid SubjectId()
        => Guid.Parse(User.FindFirstValue(AuthConstants.SubjectClaim)
            ?? throw new InvalidOperationException(
                "Authorized action without a subject claim; the permission pipeline must have rejected the ticket."));
}
