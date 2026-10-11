using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Stockpile.Api.RateLimiting;
using Stockpile.Application.Authentication;
using Stockpile.Contracts.Permissions;

namespace Stockpile.Api.Controllers;

/// <summary>
/// Task 1.39 — the bounded platform-administration surface (security-test-matrix
/// F7). Every action is gated by the task 1.41/1.42 permission pipeline: a
/// permission-constant policy (<see cref="PermissionPolicyProvider"/>), never a
/// role name, with the caller's grants resolved server-side on each request.
/// </summary>
[ApiController]
[ApiVersion(1.0)]
[Route("api/v{version:apiVersion}/admin")]
public sealed class AdminController : ControllerBase
{
    private readonly AuthenticationService _auth;

    public AdminController(AuthenticationService auth) => _auth = auth;

    /// <summary>
    /// Admin unlock (threat-model §13.4 line 168, security-test-matrix F7):
    /// clears the lockout state — end, failed count AND the backoff episode,
    /// re-arming the ladder at its base — so the account signs in immediately.
    /// Gated by the <c>admin.users.unlock</c> permission (layer 2 policy over
    /// the seeded role grants): any lesser principal — financeOperator, a
    /// buyer, a supplier — answers 403 <c>INSUFFICIENT_CAPABILITY</c>; an
    /// unknown id answers 404 <c>RESOURCE_NOT_FOUND</c>. Idempotent:
    /// unlocking an already unlocked account still answers 204.
    /// </summary>
    [HttpPost("users/{publicId:guid}/unlock")]
    [Authorize(Policy = Permissions.AdminUsersUnlock)]
    [EnableRateLimiting(RateLimitPolicies.AdminUnlock)] // §13.7: 30 / hour; rejection clusters alert via the Sensitive burst monitor
    public async Task<IActionResult> UnlockUser(Guid publicId, CancellationToken cancellationToken)
    {
        await _auth.UnlockUserAsync(publicId, cancellationToken);
        return NoContent();
    }
}
