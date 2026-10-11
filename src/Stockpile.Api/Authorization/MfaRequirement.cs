using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Stockpile.Application.Authentication;
using Stockpile.Domain.Entities.Identity;

namespace Stockpile.Api.Authorization;

/// <summary>
/// The MFA policy hook (threat-model §13.4 line 174 "policy hook", §12 risk
/// "enforcement policy added when rollout is decided", task 1.44): a caller
/// satisfies it only when the ACCOUNT's <c>TwoFactorEnabled</c> is on.
/// <para>
/// Phase 1 registers the policy under <see cref="PolicyName"/> but attaches it
/// to NO shipped action — sign-in does not challenge a code and no endpoint
/// demands MFA ("architecture ready, not enforced"). Rollout (implementation
/// plan line 353) decides where <c>[Authorize(Policy = MfaRequirement.PolicyName)]</c>
/// applies; until then the seam <c>/_test/mfa-gate</c> exercises the real
/// policy path so the hook is proven, not aspirational (X2).
/// </para>
/// The name deliberately does not collide with a <c>Permissions.All</c> code:
/// <c>PermissionPolicyProvider</c> answers explicitly registered policies
/// as-is first (branch 1), so this policy is never rewritten into a permission
/// requirement nor collapsed into the deny branch.
/// </summary>
public sealed class MfaRequirement : IAuthorizationRequirement
{
    /// <summary>The registered policy name; enforcement rollout attaches it.</summary>
    public const string PolicyName = "mfa.required";

    /// <summary>Stateless singleton: the requirement carries no per-call data.</summary>
    public static MfaRequirement Instance { get; } = new();

    private MfaRequirement()
    {
    }
}

/// <summary>
/// Evaluates <see cref="MfaRequirement"/> with the same fresh-read discipline
/// as <see cref="PermissionAuthorizationHandler"/>: <c>TwoFactorEnabled</c>
/// comes from the store on the REQUEST's own scope — never from an issued
/// ticket — so an MFA state change lands on the very next call. Deny by
/// default: an unauthenticated caller, a ticket without a resolvable
/// <c>sub</c> (the fixed <c>/_test/signin</c> seam principal), or a request
/// without an ambient HTTP context satisfies nothing — the requirement is
/// simply never marked successful (401 anonymous, 403 signed-in).
/// </summary>
public sealed class MfaAuthorizationHandler(IHttpContextAccessor accessor)
    : AuthorizationHandler<MfaRequirement>
{
    /// <inheritdoc />
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, MfaRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated != true
            || context.User.FindFirst(AuthConstants.SubjectClaim) is not { Value: { } subject }
            || !Guid.TryParse(subject, out Guid userId))
        {
            return; // deny — nothing to resolve an MFA state against
        }

        if (accessor.HttpContext is not { } httpContext)
        {
            return; // deny — no request scope means no resolution path, never a pass
        }

        var users = httpContext.RequestServices.GetRequiredService<UserManager<User>>();
        User? user = await users.FindByIdAsync(userId.ToString());

        if (user is not null && await users.GetTwoFactorEnabledAsync(user))
        {
            context.Succeed(requirement);
        }
    }
}
