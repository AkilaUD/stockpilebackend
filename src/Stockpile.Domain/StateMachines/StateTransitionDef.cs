namespace Stockpile.Domain.StateMachines;

/// <summary>
/// One declared transition of a workflow state machine (architecture §6).
/// Transitions are data, not code paths: the same table drives
/// <c>StateMachine.Evaluate()</c> at runtime and the seeded <c>StateTransition</c>
/// rows the admin UI reads.
/// </summary>
/// <typeparam name="TState">The workflow's enum state type.</typeparam>
/// <param name="From">Source state; null = any state (wildcard edges such as "any → Declined").</param>
/// <param name="To">Target state.</param>
/// <param name="PermissionCode">
/// Capability evaluated for non-system actors; null = system-only edge
/// (scheduler, matching pipeline). Codes match <c>Contracts.Permissions</c>
/// literals — asserted by an architecture test.
/// </param>
/// <param name="RequiresReason">A reason string is mandatory for this transition.</param>
/// <param name="IsForbidden">Explicitly rejected edge, so the evaluator can answer with a specific error.</param>
/// <param name="Notes">Spec annotations ("notify supplier", "reserved", "system").</param>
public sealed record StateTransitionDef<TState>(
    TState? From,
    TState To,
    string? PermissionCode = null,
    bool RequiresReason = false,
    bool IsForbidden = false,
    string? Notes = null)
    where TState : struct, Enum;
