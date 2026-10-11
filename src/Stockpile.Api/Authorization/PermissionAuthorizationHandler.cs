using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Stockpile.Application.Authentication;

namespace Stockpile.Api.Authorization;

/// <summary>
/// Evaluates a <see cref="PermissionRequirement"/> (architecture §8.2 layer 2):
/// the permission set is loaded from <see cref="IUserPermissionStore"/> on the
/// request's own scope — the union of grants on every seeded role the caller
/// holds — so a grant revoked right now is denied on the very next request and
/// no issued ticket can carry stale capability (task 1.41, api.md §2.13).
/// Deny by default: an unauthenticated caller, a ticket without a resolvable
/// <c>sub</c> claim (the fixed <c>/_test/signin</c> seam principal), or a
/// request without an ambient HTTP context satisfies nothing — the requirement
/// is simply never marked successful, so the middleware answers 401 (anonymous)
/// or 403 <c>INSUFFICIENT_CAPABILITY</c> (signed in).
/// </summary>
public sealed class PermissionAuthorizationHandler(IHttpContextAccessor accessor)
    : AuthorizationHandler<PermissionRequirement>
{
    /// <inheritdoc />
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated != true
            || context.User.FindFirst(AuthConstants.SubjectClaim) is not { Value: { } subject }
            || !Guid.TryParse(subject, out Guid userId))
        {
            return; // deny — nothing to resolve a grant against
        }

        // The ambient context carries the request whose scoped services hold
        // the permission store. No request context means no resolution path,
        // which is a deny, never a pass.
        if (accessor.HttpContext is not { } httpContext)
        {
            return; // deny
        }

        var store = httpContext.RequestServices.GetRequiredService<IUserPermissionStore>();
        IReadOnlyList<string> codes = await store.GetPermissionCodesAsync(
            userId, httpContext.RequestAborted);

        if (codes.Contains(requirement.Code, StringComparer.Ordinal))
        {
            context.Succeed(requirement);
        }
    }
}
