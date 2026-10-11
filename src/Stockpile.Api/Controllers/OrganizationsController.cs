using System.Security.Claims;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Stockpile.Api.RateLimiting;
using Stockpile.Application.Authentication;
using Stockpile.Application.Organizations;
using Stockpile.Contracts.Organizations;
using Stockpile.Contracts.Permissions;

namespace Stockpile.Api.Controllers;

/// <summary>
/// Task 1.43 — organisation surfaces (implementation-plan 1.43, business-flows
/// §2 step 3, security-test-matrix P9). Creation is the buyer-registration
/// wizard's final step: one atomic POST of the company, its addresses and
/// optional team invitations. Member addition is gated twice — the
/// <c>organization.member.manage</c> permission over the caller's grants
/// (layer 2) and, inside the use case, the caller's ACCEPTED membership of the
/// target organisation (layer 3): a non-member holding the capability gets the
/// BOLA 404, never a hint that the target exists (api.md §1.6).
/// </summary>
[ApiController]
[ApiVersion(1.0)]
[Route("api/v{version:apiVersion}/organizations")]
public sealed class OrganizationsController : ControllerBase
{
    private readonly OrganizationService _organizations;

    public OrganizationsController(OrganizationService organizations) => _organizations = organizations;

    /// <summary>
    /// Wizard step 3 (business-flows §2): create the buyer organisation, its
    /// addresses and team invitations atomically. The caller joins immediately
    /// as the configured creator role (an accepted member); invited members land
    /// as Pending rows that only an invite-accept flow can activate (T6.4).
    /// </summary>
    [HttpPost]
    [Authorize]
    [EnableRateLimiting(RateLimitPolicies.OrganizationCreate)] // §1.8: 10 / hour per subject
    public async Task<IActionResult> Create(
        [FromBody] CreateOrganizationRequest request, CancellationToken cancellationToken)
    {
        OrganizationResponse organization =
            await _organizations.CreateOrganizationAsync(SubjectId(), request, cancellationToken);
        return Created($"/api/v1/organizations/{organization.Id}", organization);
    }

    /// <summary>
    /// Add a team member (security-test-matrix P9): invite an existing account
    /// by email with an organisation-scope role. A <c>platformAdministrator</c>
    /// (or any supplier-scope) <c>roleId</c> is rejected — only organisation
    /// roles are assignable; the reject is validated at both the request filter
    /// and the authoritative use case (T6.3).
    /// </summary>
    [HttpPost("{organizationId:guid}/members")]
    [Authorize(Policy = Permissions.OrganizationMemberManage)]
    [EnableRateLimiting(RateLimitPolicies.OrganizationMemberInvite)] // §1.8: 30 / hour per subject
    public async Task<IActionResult> AddMember(
        Guid organizationId, [FromBody] CreateOrganizationMemberRequest request, CancellationToken cancellationToken)
    {
        OrganizationMemberResponse member =
            await _organizations.AddMemberAsync(SubjectId(), organizationId, request, cancellationToken);
        return Created($"/api/v1/organizations/{organizationId}/members/{member.UserId}", member);
    }

    /// <summary>The authenticated subject; [Authorize] guarantees it — any ticket without a resolvable sub was denied at the pipeline (1.41).</summary>
    private Guid SubjectId()
        => Guid.Parse(User.FindFirstValue(AuthConstants.SubjectClaim)
            ?? throw new InvalidOperationException(
                "Authorized action without a subject claim; the permission pipeline must have rejected the ticket."));
}