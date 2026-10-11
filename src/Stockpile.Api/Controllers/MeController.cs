using System.Security.Claims;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Stockpile.Api.RateLimiting;
using Stockpile.Application.Authentication;
using Stockpile.Application.Organizations;
using Stockpile.Contracts.Organizations;

namespace Stockpile.Api.Controllers;

/// <summary>
/// Task 1.43 — the signed-in user's own surfaces (api.md §2.2): their address
/// book. Every action is scoped to the caller's own rows by user id — another
/// user's address (or a malformed id) answers the same 404 so existence is
/// never inferable across principals (BOLA, api.md §1.6). The anonymous
/// variants of these routes are the F3 wire test's subject.
/// </summary>
[ApiController]
[ApiVersion(1.0)]
[Route("api/v{version:apiVersion}/me")]
public sealed class MeController : ControllerBase
{
    private readonly OrganizationService _organizations;

    public MeController(OrganizationService organizations) => _organizations = organizations;

    [HttpGet("addresses")]
    [Authorize]
    public async Task<ActionResult<IReadOnlyList<AddressResponse>>> GetAddresses(CancellationToken cancellationToken)
        => Ok(await _organizations.GetAddressesAsync(SubjectId(), cancellationToken));

    [HttpPost("addresses")]
    [Authorize]
    [EnableRateLimiting(RateLimitPolicies.AddressWrite)] // §1.8: 60 / hour per subject
    public async Task<ActionResult<AddressResponse>> CreateAddress(
        [FromBody] AddressDraft request, CancellationToken cancellationToken)
    {
        AddressResponse address = await _organizations.CreateAddressAsync(SubjectId(), request, cancellationToken);
        return Created($"/api/v1/me/addresses/{address.Id}", address);
    }

    [HttpPatch("addresses/{addressId:guid}")]
    [Authorize]
    [EnableRateLimiting(RateLimitPolicies.AddressWrite)] // §1.8: 60 / hour per subject
    public async Task<ActionResult<AddressResponse>> UpdateAddress(
        Guid addressId, [FromBody] AddressDraft request, CancellationToken cancellationToken)
        => Ok(await _organizations.UpdateAddressAsync(SubjectId(), addressId, request, cancellationToken));

    [HttpDelete("addresses/{addressId:guid}")]
    [Authorize]
    [EnableRateLimiting(RateLimitPolicies.AddressWrite)] // §1.8: 60 / hour per subject
    public async Task<IActionResult> DeleteAddress(Guid addressId, CancellationToken cancellationToken)
    {
        await _organizations.DeleteAddressAsync(SubjectId(), addressId, cancellationToken);
        return NoContent();
    }

    /// <summary>The authenticated subject; [Authorize] guarantees it — any ticket without a resolvable sub was denied at the pipeline (1.41).</summary>
    private Guid SubjectId()
        => Guid.Parse(User.FindFirstValue(AuthConstants.SubjectClaim)
            ?? throw new InvalidOperationException(
                "Authorized action without a subject claim; the permission pipeline must have rejected the ticket."));
}