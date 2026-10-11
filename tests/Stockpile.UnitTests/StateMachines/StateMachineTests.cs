using Stockpile.Domain.Enums;
using Stockpile.Domain.StateMachines;

namespace Stockpile.UnitTests.StateMachines;

/// <summary>
/// Task 1.30 — the single transition evaluator (architecture §7.1,
/// domain-model §11): declared edges allow, forbidden edges reject (§64),
/// capability is checked against the actor's permission set (system actor
/// exempt), required reasons are enforced, unknown edges are a spec gap, and
/// the transition tables keep the one-edge-per-(from,to) invariant.
/// </summary>
public sealed class StateMachineTests
{
    private static readonly StateMachine<QuoteStatus> Quotes = new(TransitionTables.Quote.All);
    private static readonly StateMachine<RFQMatchStatus> Matches = new(TransitionTables.RfqMatch.All);
    private static readonly StateMachine<RFQStatus> Rfqs = new(TransitionTables.Rfq.All);

    [Fact]
    public void Declared_edge_allows_a_user_with_the_permission()
    {
        var actor = ActorContext.ForUser(Guid.NewGuid(), new HashSet<string> { "quote.submit" });

        TransitionResult<QuoteStatus> result = Quotes.Evaluate(QuoteStatus.Draft, QuoteStatus.Submitted, actor, null);

        Assert.True(result.IsAllowed);
        Assert.Equal(TransitionResultKind.Allowed, result.Kind);
        Assert.Equal("quote.submit", result.Edge?.PermissionCode);
    }

    [Fact]
    public void System_actor_passes_system_only_edges()
    {
        TransitionResult<QuoteStatus> result = Quotes.Evaluate(
            QuoteStatus.Submitted, QuoteStatus.UnderReview, ActorContext.System, null);

        Assert.True(result.IsAllowed);
    }

    [Fact]
    public void Permission_denied_when_the_capability_is_missing()
    {
        var buyerWithoutCapability = ActorContext.ForUser(Guid.NewGuid(), new HashSet<string> { "rfq.read.own" });

        TransitionResult<QuoteStatus> result = Quotes.Evaluate(
            QuoteStatus.Submitted, QuoteStatus.Accepted, buyerWithoutCapability, null);

        Assert.Equal(TransitionResultKind.PermissionDenied, result.Kind);
        Assert.Equal("quote.accept", result.Edge?.PermissionCode);
    }

    [Fact]
    public void A_user_cannot_take_a_system_only_edge()
    {
        var regularUser = ActorContext.ForUser(Guid.NewGuid(), new HashSet<string> { "quote.read.buyer" });

        // Submitted → Expired declares no permission code: it is system-only
        // (ValidUntil sweeps); a regular user carrying even quote.read.buyer
        // must be denied.
        TransitionResult<QuoteStatus> result = Quotes.Evaluate(
            QuoteStatus.Submitted, QuoteStatus.Expired, regularUser, null);

        Assert.Equal(TransitionResultKind.PermissionDenied, result.Kind);
        Assert.Null(result.Edge?.PermissionCode);
    }

    [Fact]
    public void Reason_is_required_for_reason_edges_and_satisfied_when_supplied()
    {
        // RFQ Draft → Cancelled requires a reason (rfq.cancel).
        var actor = ActorContext.ForUser(Guid.NewGuid(), new HashSet<string> { "rfq.cancel" });

        TransitionResult<RFQStatus> missing = Rfqs.Evaluate(RFQStatus.Draft, RFQStatus.Cancelled, actor, null);
        Assert.Equal(TransitionResultKind.ReasonRequired, missing.Kind);

        TransitionResult<RFQStatus> blank = Rfqs.Evaluate(RFQStatus.Draft, RFQStatus.Cancelled, actor, "   ");
        Assert.Equal(TransitionResultKind.ReasonRequired, blank.Kind);

        TransitionResult<RFQStatus> supplied = Rfqs.Evaluate(RFQStatus.Draft, RFQStatus.Cancelled, actor, "Requirements withdrawn");
        Assert.True(supplied.IsAllowed);
    }

    [Fact]
    public void Forbidden_edges_are_rejected_before_capability_is_checked()
    {
        // §64: accepting an expired quote is a declared dead end even for an operator.
        var operatorActor = ActorContext.ForUser(Guid.NewGuid(), new HashSet<string> { "quote.accept" });

        TransitionResult<QuoteStatus> result = Quotes.Evaluate(
            QuoteStatus.Expired, QuoteStatus.Accepted, operatorActor, null);

        Assert.Equal(TransitionResultKind.Forbidden, result.Kind);
        Assert.True(result.Edge?.IsForbidden);
    }

    [Fact]
    public void Wildcard_edges_match_from_any_state()
    {
        var actor = ActorContext.ForUser(Guid.NewGuid(), new HashSet<string> { "rfq.match.decline" });

        // Deleted/Notified/Viewed → Declined (any → Declined, reason required).
        TransitionResult<RFQMatchStatus> fromMatched = Matches.Evaluate(
            RFQMatchStatus.Matched, RFQMatchStatus.Declined, actor, "duplicate lead");
        TransitionResult<RFQMatchStatus> fromQuoting = Matches.Evaluate(
            RFQMatchStatus.Quoting, RFQMatchStatus.Declined, actor, "not in scope");

        Assert.True(fromMatched.IsAllowed);
        Assert.True(fromQuoting.IsAllowed);
    }

    [Fact]
    public void Undeclared_edges_are_a_spec_gap_not_a_client_error()
    {
        var operatorActor = ActorContext.ForUser(Guid.NewGuid(), new HashSet<string> { "quote.withdraw" });

        // Expired → Withdrawn has no edge in the quote table.
        TransitionResult<QuoteStatus> absent = Quotes.Evaluate(
            QuoteStatus.Expired, QuoteStatus.Withdrawn, operatorActor, null);

        Assert.Equal(TransitionResultKind.NotDeclared, absent.Kind);
        Assert.Null(absent.Edge);
    }

    [Fact]
    public void Every_machine_has_at_most_one_edge_per_from_to_pair()
    {
        // Table invariant consumed by FindEdge's explicit-wins-over-wildcard rule.
        var seen = new HashSet<string>();

        foreach ((string entity, StateTransitionRow row) in TransitionTables.AllMachines())
        {
            string key = $"{entity}|{row.From}|{row.To}";
            Assert.True(seen.Add(key), $"Duplicate transition edge '{key}' in the {entity} machine.");
        }
    }
}
