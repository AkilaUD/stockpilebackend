namespace Stockpile.Domain.StateMachines;

/// <summary>
/// Who asks <see cref="StateMachine{TState}.Evaluate"/> for a transition
/// (architecture §7.1). Two shapes only:
/// * the system actor — the scheduler, matching pipeline, billing sweep, or
///   webhook handlers — which is exempt from capability checks (those edges
///   declare <c>PermissionCode = null</c> and are system-only by contract);
/// * a user with their granted permission codes, evaluated against the edge's
///   code. The permission set is the caller's responsibility to load (role /
///   permission claims at Phase 1D); this record merely carries it.
/// </summary>
public sealed record ActorContext(Guid? UserId, IReadOnlySet<string>? Permissions)
{
    /// <summary>The system actor: no user, capability checks always pass.</summary>
    public static ActorContext System { get; } = new(null, null);

    /// <summary>A real user acting with the supplied capability set.</summary>
    public static ActorContext ForUser(Guid userId, IReadOnlySet<string> permissions) => new(userId, permissions);

    /// <summary>True when no user identity is attached (system-level call).</summary>
    public bool IsSystem => UserId is null;

    /// <summary>Whether this actor satisfies <paramref name="permissionCode"/> (null code = system-only edge).</summary>
    public bool Can(string? permissionCode) =>
        IsSystem || (permissionCode is not null && Permissions?.Contains(permissionCode) == true);
}
