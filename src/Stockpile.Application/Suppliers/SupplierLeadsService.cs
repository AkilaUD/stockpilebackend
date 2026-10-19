using Stockpile.Application.Audit;
using Stockpile.Application.Authentication;
using Stockpile.Application.Common.Errors;
using Stockpile.Contracts.Errors;
using Stockpile.Domain.Entities.Procurement;
using Stockpile.Domain.Enums;
using Stockpile.Domain.StateMachines;

namespace Stockpile.Application.Suppliers;

/// <summary>
/// Use cases for the signed-in supplier's buying-lead inbox (business-flows §6,
/// api.md §2.9). The inbox, the lead view and the two lead actions all resolve
/// the caller's supplier from their own accepted membership first, so a foreign
/// lead id matches no row and answers one 404 <c>RESOURCE_NOT_FOUND</c> —
/// another supplier's lead is never inferable (BOLA, api.md §1.6).
/// <para>
/// The lead's lifecycle moves only through <c>StateMachine&lt;RFQMatchStatus&gt;</c>
/// (domain-model §11.2, security-test-matrix X6/S10); the two edges the supplier
/// can drive are the system chain that records the open and the reason-required
/// decline.
/// </para>
/// </summary>
public sealed class SupplierLeadsService(
    ISupplierLeadsStore leads,
    ISupplierSelfStore suppliers,
    IUserPermissionStore permissions,
    IAuditService audit)
{
    private static readonly StateMachine<RFQMatchStatus> Machine = new(TransitionTables.RfqMatch.All);

    /// <summary>The inbox, newest first, cursor-paginated (api.md §1, business-flows §6).</summary>
    public async Task<SupplierLeadPageResponse> ListAsync(
        Guid userId, string? status, string? cursor, int? limit, CancellationToken cancellationToken)
    {
        Guid supplierId = await RequireSupplierAsync(userId, cancellationToken);
        SupplierLeadView? view = ParseView(status);
        SupplierLeadCursorPosition? after = ParseCursor(cursor);
        int pageSize = ParseLimit(limit);

        SupplierLeadPage page = await leads.ListAsync(supplierId, view, after, pageSize, cancellationToken);

        string? next = page.HasMore && page.Rows.Count > 0
            ? SupplierLeadCursor.Encode(page.Rows[^1].SortValue, page.Rows[^1].LeadId)
            : null;

        return new SupplierLeadPageResponse(
            page.Rows.Select(row => row.Lead).ToList(),
            next,
            page.HasMore);
    }

    /// <summary>One lead as §6 permits the supplier to see it.</summary>
    public async Task<SupplierLeadDetailResponse> GetAsync(Guid userId, Guid leadId, CancellationToken cancellationToken)
    {
        Guid supplierId = await RequireSupplierAsync(userId, cancellationToken);
        return await ReadAsync(supplierId, leadId, cancellationToken);
    }

    /// <summary>
    /// Records that the supplier opened the lead (business-flows §6): the state
    /// machine's declared chain is <c>Matched → Notified → Viewed</c>, and a
    /// supplier opening a lead is the delivery evidence the platform has — the
    /// notification consumer that would normally write <c>Notified</c> is a later
    /// phase, so the walk follows the declared system edges instead of inventing
    /// a <c>Matched → Viewed</c> one. The first open is the one that counts
    /// (§67 H response health), so a lead already at <c>Viewed</c> or beyond —
    /// or one that already resolved — keeps its state and its timestamp, and the
    /// action stays idempotent for a client that reopens it.
    /// </summary>
    public async Task<SupplierLeadDetailResponse> ViewAsync(Guid userId, Guid leadId, CancellationToken cancellationToken)
    {
        Guid supplierId = await RequireSupplierAsync(userId, cancellationToken);
        RfqMatch match = await LoadAsync(supplierId, leadId, cancellationToken);

        bool marked = false;
        if (match.Status == RFQMatchStatus.Matched)
        {
            // §11.2's own chain: Matched → Notified is a system edge, and the
            // notification timestamp itself is NOT set here — no notification was
            // dispatched, and the column must keep meaning "we notified them".
            Apply(match, RFQMatchStatus.Notified, ActorContext.System, reason: null);
        }

        if (match.Status == RFQMatchStatus.Notified)
        {
            Apply(match, RFQMatchStatus.Viewed, ActorContext.System, reason: null);
            match.ViewedAt ??= DateTime.UtcNow;
            marked = true;
        }

        if (marked)
        {
            await leads.SaveChangesAsync(cancellationToken);
            await RecordAsync(leadId, "supplier.lead.viewed", cancellationToken);
        }

        return await ReadAsync(supplierId, leadId, cancellationToken);
    }

    /// <summary>
    /// Declines the lead (§11.2 "any → Declined", reason required). The reason is
    /// also the supplier's response, so <c>RespondedAt</c> is stamped when it is
    /// still unset (§5.4 drives §67 H response health).
    /// </summary>
    public async Task<SupplierLeadDetailResponse> DeclineAsync(
        Guid userId, Guid leadId, DeclineSupplierLeadRequest request, CancellationToken cancellationToken)
    {
        Guid supplierId = await RequireSupplierAsync(userId, cancellationToken);
        RfqMatch match = await LoadAsync(supplierId, leadId, cancellationToken);

        IReadOnlySet<string> granted = (await permissions.GetPermissionCodesAsync(userId, cancellationToken))
            .ToHashSet(StringComparer.Ordinal);
        string reason = request.Reason.Trim();

        Apply(match, RFQMatchStatus.Declined, ActorContext.ForUser(userId, granted), reason);
        match.DeclineReason = reason;
        match.RespondedAt ??= DateTime.UtcNow;

        await leads.SaveChangesAsync(cancellationToken);
        await RecordAsync(leadId, "supplier.lead.declined", cancellationToken);

        return await ReadAsync(supplierId, leadId, cancellationToken);
    }

    // ---- Internals ----------------------------------------------------------

    private async Task<Guid> RequireSupplierAsync(Guid userId, CancellationToken cancellationToken)
        => await suppliers.ResolveSupplierIdAsync(userId, cancellationToken) ?? throw NotFound();

    private async Task<RfqMatch> LoadAsync(Guid supplierId, Guid leadId, CancellationToken cancellationToken)
        => await leads.LoadForUpdateAsync(supplierId, leadId, cancellationToken) ?? throw NotFound();

    private async Task<SupplierLeadDetailResponse> ReadAsync(
        Guid supplierId, Guid leadId, CancellationToken cancellationToken)
        => await leads.GetAsync(supplierId, leadId, cancellationToken) ?? throw NotFound();

    /// <summary>
    /// Applies one transition through the evaluator. A missing capability is 403;
    /// anything else — an undeclared edge, a reason the caller skipped — is the
    /// standard invalid-transition 409.
    /// </summary>
    private static void Apply(RfqMatch match, RFQMatchStatus to, ActorContext actor, string? reason)
    {
        TransitionResult<RFQMatchStatus> result = Machine.Evaluate(match.Status, to, actor, reason);
        if (!result.IsAllowed)
        {
            throw result.Kind == TransitionResultKind.PermissionDenied
                ? new AppException(ErrorCodes.InsufficientCapability)
                : new AppException(ErrorCodes.StateTransitionInvalid);
        }

        match.Status = result.Edge!.To; // the evaluator's matched edge — never a literal (X6/S10)
    }

    private Task RecordAsync(Guid leadId, string action, CancellationToken cancellationToken)
        => audit.RecordAsync(
            new AuditEntry(action, "RFQMatch", leadId.ToString(), AuditResult.Success), cancellationToken);

    /// <summary>An absent view means every lead; an unknown one is a client mistake, not an empty page.</summary>
    private static SupplierLeadView? ParseView(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return null;
        }

        if (SupplierLeadViews.ByWireName.TryGetValue(status.Trim(), out SupplierLeadView view))
        {
            return view;
        }

        throw new AppException(
            ErrorCodes.ValidationFailed,
            [
                new FieldError(
                    "status", "unknown_status",
                    $"Choose one of: {string.Join(", ", SupplierLeadViews.ByWireName.Keys)}."),
            ],
            "The inbox view is not recognised.");
    }

    private static SupplierLeadCursorPosition? ParseCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return null;
        }

        if (SupplierLeadCursor.TryDecode(cursor, out SupplierLeadCursorPosition position))
        {
            return position;
        }

        throw new AppException(
            ErrorCodes.ValidationFailed,
            [new FieldError("cursor", "invalid_cursor", "The page cursor is not valid; reload the list.")],
            "The page cursor is not valid.");
    }

    private static int ParseLimit(int? limit)
    {
        if (limit is null)
        {
            return SupplierLeadViews.DefaultLimit;
        }

        if (limit < 1 || limit > SupplierLeadViews.MaxLimit)
        {
            throw new AppException(
                ErrorCodes.ValidationFailed,
                [
                    new FieldError(
                        "limit", "out_of_range",
                        $"Choose a page size between 1 and {SupplierLeadViews.MaxLimit}."),
                ],
                "The page size is out of range.");
        }

        return limit.Value;
    }

    /// <summary>The one 404 of this surface: a caller with no supplier and a lead they do not own are indistinguishable (api.md §1.6).</summary>
    private static AppException NotFound() => new(ErrorCodes.ResourceNotFound);
}
