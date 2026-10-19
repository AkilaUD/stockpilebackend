using System.Security.Claims;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Stockpile.Api.Idempotency;
using Stockpile.Api.RateLimiting;
using Stockpile.Application.Authentication;
using Stockpile.Application.Procurement;
using Stockpile.Contracts.Permissions;
using Stockpile.Contracts.Procurement;

namespace Stockpile.Api.Controllers;

/// <summary>
/// RFQ surfaces (api.md §2.6, business-flows §3.8). Creating a draft and
/// submitting it are separate steps: the draft is the buyer's scratchpad, and
/// submit runs the matching pipeline. Every read is scoped to the caller's
/// organisations by the use case — another tenant's id answers the same 404, so
/// existence is never inferable across buyers (BOLA, api.md §1.6).
/// </summary>
[ApiController]
[ApiVersion(1.0)]
[Route("api/v{version:apiVersion}/rfqs")]
public sealed class RfqsController : ControllerBase
{
    private readonly RfqService _rfqs;

    public RfqsController(RfqService rfqs) => _rfqs = rfqs;

    /// <summary>
    /// Create a Draft RFQ with its line items (api.md §2.6). Capability is
    /// <c>rfq.create</c> over the caller's grants; a buyer with no organisation
    /// yet supplies company details and the use case provisions the buyer
    /// company before persisting (business-flows §2 step 3).
    /// </summary>
    [HttpPost]
    [Authorize(Policy = Permissions.RfqCreate)]
    [EnableRateLimiting(RateLimitPolicies.RfqCreate)] // §1.8: 20 / hour per subject
    public async Task<IActionResult> Create(
        [FromBody] CreateRfqRequest request, CancellationToken cancellationToken)
    {
        RfqResponse rfq = await _rfqs.DraftAsync(SubjectId(), request, cancellationToken);
        return Created($"/api/v1/rfqs/{rfq.Id}", rfq);
    }

    /// <summary>
    /// Submit a draft (api.md §2.6) — the Draft → Submitted edge runs under the
    /// caller's own capability (<c>rfq.submit</c>), then the platform's matching
    /// pipeline drives Submitted → Matching → Dispatched. A non-draft answers
    /// the standard business conflict (409). Requires <c>Idempotency-Key</c>
    /// (api.md §1.7) so retries replay instead of re-matching.
    /// </summary>
    [HttpPost("{rfqId:guid}/submit")]
    [Idempotent]
    [Authorize(Policy = Permissions.RfqSubmit)]
    [EnableRateLimiting(RateLimitPolicies.RfqSubmit)] // §1.8: 10 / hour per subject
    public async Task<IActionResult> Submit(Guid rfqId, CancellationToken cancellationToken)
    {
        RfqResponse rfq = await _rfqs.SubmitAsync(SubjectId(), rfqId, cancellationToken);
        HttpContext.Items[IdempotencyFilter.ReferenceItemsKey] = rfq.Id;
        return Ok(rfq);
    }

    /// <summary>One RFQ that the caller's organisation owns (api.md §2.6 <c>GET /rfqs/{id}</c>).</summary>
    [HttpGet("{rfqId:guid}")]
    [Authorize(Policy = Permissions.RfqReadOwn)]
    public async Task<IActionResult> Get(Guid rfqId, CancellationToken cancellationToken)
        => Ok(await _rfqs.GetAsync(SubjectId(), rfqId, cancellationToken));

    /// <summary>The authenticated subject; [Authorize] guarantees it — any ticket without a resolvable sub was denied at the pipeline (1.41).</summary>
    private Guid SubjectId()
        => Guid.Parse(User.FindFirstValue(AuthConstants.SubjectClaim)
            ?? throw new InvalidOperationException(
                "Authorized action without a subject claim; the permission pipeline must have rejected the ticket."));
}
