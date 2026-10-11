using Microsoft.AspNetCore.Authorization;

namespace Stockpile.Api.Authorization;

/// <summary>
/// Layer 2 of the five-layer authorization pipeline (architecture §8.2,
/// security-test-matrix F10/X2, threat-model T6.1): the caller must hold the
/// permission <see cref="Code"/> — a constant from
/// <see cref="Stockpile.Contracts.Permissions.Permissions"/> — to satisfy the
/// policy the <see cref="PermissionPolicyProvider"/> built from it.
/// Capability only: the grant is resolved server-side from the caller's
/// seeded role bindings on every request (never from an issued claim), and
/// object ownership is still confirmed separately by the layer-4 ownership
/// query of whatever aggregate the endpoint touches.
/// </summary>
public sealed class PermissionRequirement(string code) : IAuthorizationRequirement
{
    /// <summary>The canonical permission code this policy demands (api.md §2.13).</summary>
    public string Code { get; } = code;
}
