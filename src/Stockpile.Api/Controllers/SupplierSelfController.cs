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
/// The signed-in supplier's own surface (api.md §2.9) — the resumable stages of
/// supplier onboarding after the account exists (business-flows §3.3 stages
/// 2–5): the contact/business profile, the district service areas and the
/// category coverage. The caller's supplier is resolved from their own accepted
/// membership, and every answer is that supplier's state, so one supplier can
/// never read or write another's rows; a caller without a supplier answers one
/// 404 (BOLA, api.md §1.6). The profile, coverage and coverage-removal routes
/// require the owner capability <c>supplier.update.own</c>; the summary read is
/// open to any member of the supplier.
/// </summary>
[ApiController]
[ApiVersion(1.0)]
[Route("api/v{version:apiVersion}/suppliers/me")]
[Authorize]
public sealed class SupplierSelfController(SupplierSelfService service) : ControllerBase
{
    /// <summary>The caller's supplier as the onboarding surface reads it.</summary>
    [HttpGet]
    public async Task<ActionResult<SupplierSelfResponse>> GetSelf(CancellationToken ct)
        => Ok(await service.GetSelfAsync(SubjectId(), ct));

    /// <summary>Merges stages 2–3 (person + business) and answers with the new state.</summary>
    [HttpPatch("profile")]
    [Authorize(Policy = Permissions.SupplierUpdateOwn)]
    [EnableRateLimiting(RateLimitPolicies.SupplierProfileWrite)]
    public async Task<ActionResult<SupplierSelfResponse>> UpdateProfile(
        [FromBody] UpdateSupplierProfileRequest request, CancellationToken ct)
        => Ok(await service.UpdateProfileAsync(SubjectId(), request, ct));

    /// <summary>Adds one category to the supplier's coverage (stage 5, idempotent).</summary>
    [HttpPost("categories")]
    [Authorize(Policy = Permissions.SupplierUpdateOwn)]
    [EnableRateLimiting(RateLimitPolicies.SupplierProfileWrite)]
    public async Task<ActionResult<SupplierSelfResponse>> AddCategory(
        [FromBody] AddSupplierCategoryRequest request, CancellationToken ct)
        => Ok(await service.AddCategoryAsync(SubjectId(), request.Slug, ct));

    /// <summary>Removes one category from the supplier's coverage (stage 5).</summary>
    [HttpDelete("categories/{**slug}")]
    [Authorize(Policy = Permissions.SupplierUpdateOwn)]
    [EnableRateLimiting(RateLimitPolicies.SupplierProfileWrite)]
    public async Task<ActionResult<SupplierSelfResponse>> RemoveCategory(string slug, CancellationToken ct)
        => Ok(await service.RemoveCategoryAsync(SubjectId(), slug, ct));

    /// <summary>Replaces the supplier's district service areas (stage 4).</summary>
    [HttpPut("service-areas")]
    [Authorize(Policy = Permissions.SupplierUpdateOwn)]
    [EnableRateLimiting(RateLimitPolicies.SupplierProfileWrite)]
    public async Task<ActionResult<SupplierSelfResponse>> SetServiceAreas(
        [FromBody] SetSupplierServiceAreasRequest request, CancellationToken ct)
        => Ok(await service.SetServiceAreasAsync(SubjectId(), request, ct));

    /// <summary>The authenticated subject; [Authorize] guarantees it (1.41).</summary>
    private Guid SubjectId()
        => Guid.Parse(User.FindFirstValue(AuthConstants.SubjectClaim)
            ?? throw new InvalidOperationException(
                "Authorized action without a subject claim; the permission pipeline must have rejected the ticket."));
}
