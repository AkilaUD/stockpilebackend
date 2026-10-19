using System.Security.Claims;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Stockpile.Api.RateLimiting;
using Stockpile.Application.Authentication;
using Stockpile.Application.Organizations;
using Stockpile.Application.Procurement;
using Stockpile.Contracts.Authentication;
using Stockpile.Contracts.Organizations;
using Stockpile.Contracts.Permissions;
using Stockpile.Contracts.Procurement;

namespace Stockpile.Api.Controllers;

/// <summary>
/// Task 1.43 — the signed-in user's own surfaces (api.md §2.2): their address
/// book, professional profile (wizard step 2), preferences (wizard step 4a) and
/// notification preferences (wizard step 4b). Every action is scoped to the
/// caller's own rows by user id — another user's data (or a malformed id)
/// answers the same 404 so existence is never inferable across principals
/// (BOLA, api.md §1.6). The anonymous variants of these routes are the F3
/// wire test's subject.
/// </summary>
[ApiController]
[ApiVersion(1.0)]
[Route("api/v{version:apiVersion}/me")]
public sealed class MeController : ControllerBase
{
    private readonly OrganizationService _organizations;
    private readonly RfqService _rfqs;
    private readonly UserProfileService _profile;

    public MeController(OrganizationService organizations, RfqService rfqs, UserProfileService profile)
    {
        _organizations = organizations;
        _rfqs = rfqs;
        _profile = profile;
    }

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

    /// <summary>The caller's organisations' RFQs, newest first (api.md §2.6 <c>GET /me/rfqs</c>).</summary>
    [HttpGet("rfqs")]
    [Authorize(Policy = Permissions.RfqReadOwn)]
    public async Task<ActionResult<IReadOnlyList<RfqResponse>>> GetRfqs(CancellationToken cancellationToken)
        => Ok(await _rfqs.ListAsync(SubjectId(), cancellationToken));

    // ---- Profile (wizard step 2, api.md §2.2) ----------------------------------

    /// <summary>Gets the caller's professional profile (wizard step 2 read).</summary>
    [HttpGet("profile")]
    [Authorize]
    public async Task<ActionResult<ProfileResponse>> GetProfile(CancellationToken ct)
        => Ok(await _profile.GetProfileAsync(SubjectId(), ct));

    /// <summary>Updates the caller's professional profile (wizard step 2 submit).</summary>
    [HttpPatch("profile")]
    [Authorize]
    [EnableRateLimiting(RateLimitPolicies.MeProfileWrite)]
    public async Task<ActionResult<ProfileResponse>> UpdateProfile(
        [FromBody] ProfileDraft draft, CancellationToken ct)
        => Ok(await _profile.UpdateProfileAsync(SubjectId(), draft, ct));

    // ---- Preferences (wizard step 4a, api.md §2.2) ------------------------------

    /// <summary>Gets the caller's UI/locale preferences (wizard step 4a read).</summary>
    [HttpGet("preferences")]
    [Authorize]
    public async Task<ActionResult<PreferencesResponse>> GetPreferences(CancellationToken ct)
        => Ok(await _profile.GetPreferencesAsync(SubjectId(), ct));

    /// <summary>Updates the caller's UI/locale preferences (wizard step 4a submit).</summary>
    [HttpPatch("preferences")]
    [Authorize]
    [EnableRateLimiting(RateLimitPolicies.MePreferencesWrite)]
    public async Task<ActionResult<PreferencesResponse>> UpdatePreferences(
        [FromBody] PreferencesDraft draft, CancellationToken ct)
        => Ok(await _profile.UpdatePreferencesAsync(SubjectId(), draft, ct));

    // ---- Notification Preferences (wizard step 4b, api.md §2.2) -----------------

    /// <summary>Gets the caller's per-channel notification preferences (wizard step 4b read).</summary>
    [HttpGet("notification-preferences")]
    [Authorize]
    public async Task<ActionResult<NotificationPreferencesResponse>> GetNotificationPreferences(CancellationToken ct)
        => Ok(await _profile.GetNotificationPreferencesAsync(SubjectId(), ct));

    /// <summary>Updates the caller's per-channel notification preferences (wizard step 4b submit).</summary>
    [HttpPatch("notification-preferences")]
    [Authorize]
    [EnableRateLimiting(RateLimitPolicies.MeNotificationPreferencesWrite)]
    public async Task<ActionResult<NotificationPreferencesResponse>> UpdateNotificationPreferences(
        [FromBody] NotificationPreferencesDraft draft, CancellationToken ct)
        => Ok(await _profile.UpdateNotificationPreferencesAsync(SubjectId(), draft, ct));

    /// <summary>The authenticated subject; [Authorize] guarantees it — any ticket without a resolvable sub was denied at the pipeline (1.41).</summary>
    private Guid SubjectId()
        => Guid.Parse(User.FindFirstValue(AuthConstants.SubjectClaim)
            ?? throw new InvalidOperationException(
                "Authorized action without a subject claim; the permission pipeline must have rejected the ticket."));
}