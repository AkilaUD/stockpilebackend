using Microsoft.AspNetCore.Authorization;

namespace Stockpile.Api.Authorization;

/// <summary>
/// The unsatisfiable requirement an unknown policy name resolves to (deny by
/// default, threat-model T6.1): <see cref="PermissionPolicyProvider"/> maps any
/// name that is neither a registered policy nor a
/// <see cref="Stockpile.Contracts.Permissions.Permissions"/> code onto it, so a
/// typo'd <c>[Authorize(Policy = …)]</c> fails CLOSED at runtime — 401 for an
/// anonymous caller, 403 <c>INSUFFICIENT_CAPABILITY</c> for a signed-in one —
/// while the F10 architecture test fails the build before it is reachable.
/// </summary>
public sealed class DenyRequirement : IAuthorizationRequirement
{
    /// <summary>Stateless singleton: the requirement carries no per-call data.</summary>
    public static DenyRequirement Instance { get; } = new();

    private DenyRequirement()
    {
    }
}

/// <summary>Handles <see cref="DenyRequirement"/> by failing every caller.</summary>
public sealed class DenyAuthorizationHandler : AuthorizationHandler<DenyRequirement>
{
    /// <inheritdoc />
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, DenyRequirement requirement)
    {
        context.Fail();
        return Task.CompletedTask;
    }
}
