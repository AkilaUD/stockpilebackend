namespace Stockpile.Domain.StateMachines;

/// <summary>
/// The outcome of <see cref="StateMachine{TState}.Evaluate"/> (architecture
/// §7.1). Allowed is the ONLY green signal; every other kind carries the
/// matched edge (when one exists) so handlers get a precise business error —
/// 403 <c>INSUFFICIENT_CAPABILITY</c>, 403 reason-required, 409
/// <c>TRANSITION_NOT_PERMITTED</c>, and the upstream 5xx for a NotDeclared
/// edge (a spec gap, never a client error).
/// </summary>
public sealed record TransitionResult<TState>(TransitionResultKind Kind, StateTransitionDef<TState>? Edge)
    where TState : struct, Enum
{
    public bool IsAllowed => Kind == TransitionResultKind.Allowed;
}

/// <summary>Every possible evaluation outcome, in severity order for error mapping.</summary>
public enum TransitionResultKind
{
    Allowed,
    NotDeclared,
    Forbidden,
    PermissionDenied,
    ReasonRequired,
}
