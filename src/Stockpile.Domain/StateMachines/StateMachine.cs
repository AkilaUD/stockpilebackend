namespace Stockpile.Domain.StateMachines;

/// <summary>
/// The single transition evaluator for every workflow (architecture §7.1,
/// domain-model §11; security-test-matrix X6/S10). Pure and host-agnostic: no
/// database, no clock, no audit side effect. The transition table is injected
/// so the same evaluator drives the real <c>TransitionTables</c>, admin UI
/// display, and unit fixtures.
///
/// Evaluation order — a single edge is matched (explicit <c>From</c> wins over
/// a wildcard; exactly one edge per (from, to) pair is a table invariant
/// asserted by tests) and then checked, in order:
/// 1. <c>IsForbidden</c> → the edge is a declared dead end (§64);
/// 2. capability: the actor must satisfy the edge's permission code
///    (system actor always can);
/// 3. reason: a <c>RequiresReason</c> edge demands a non-blank reason.
///
/// On <see cref="TransitionResultKind.Allowed"/> the CALLER emits the §11.11
/// status-history/audit record (actor, from, to, permission evaluated, reason,
/// timestamp UTC, correlation id) — the edge on the result carries the
/// permission code so no handler can forget it. This keeps five §7
/// requirements non-optional (architecture D4).
/// </summary>
public sealed class StateMachine<TState>(IReadOnlyList<StateTransitionDef<TState>> transitions)
    where TState : struct, Enum
{
    private readonly IReadOnlyList<StateTransitionDef<TState>> _transitions =
        transitions ?? throw new ArgumentNullException(nameof(transitions));

    /// <summary>
    /// Evaluates <c>from → to</c> for <paramref name="actor"/>.
    /// </summary>
    public TransitionResult<TState> Evaluate(TState from, TState to, ActorContext actor, string? reason)
    {
        ArgumentNullException.ThrowIfNull(actor);

        StateTransitionDef<TState>? edge = FindEdge(from, to);
        if (edge is null)
        {
            return new TransitionResult<TState>(TransitionResultKind.NotDeclared, null);
        }

        if (edge.IsForbidden)
        {
            return new TransitionResult<TState>(TransitionResultKind.Forbidden, edge);
        }

        if (!actor.Can(edge.PermissionCode))
        {
            return new TransitionResult<TState>(TransitionResultKind.PermissionDenied, edge);
        }

        if (edge.RequiresReason && string.IsNullOrWhiteSpace(reason))
        {
            return new TransitionResult<TState>(TransitionResultKind.ReasonRequired, edge);
        }

        return new TransitionResult<TState>(TransitionResultKind.Allowed, edge);
    }

    /// <summary>
    /// Matches the edge for (from, to): an explicit <c>From</c> beats a
    /// wildcard; the wildcard is the fallback. Exactly one candidate per
    /// (from, to) is the table invariant (unit-tested via
    /// <c>TransitionTables.AllMachines</c>).
    /// </summary>
    private StateTransitionDef<TState>? FindEdge(TState from, TState to)
    {
        StateTransitionDef<TState>? wildcard = null;

        foreach (StateTransitionDef<TState> edge in _transitions)
        {
            if (!EqualityComparer<TState>.Default.Equals(edge.To, to))
            {
                continue;
            }

            if (edge.From is null)
            {
                wildcard ??= edge;
                continue;
            }

            if (EqualityComparer<TState>.Default.Equals(edge.From.Value, from))
            {
                return edge;
            }
        }

        return wildcard;
    }
}
