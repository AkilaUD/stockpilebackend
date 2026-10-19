using Stockpile.Domain.Enums;

namespace Stockpile.Application.Suppliers;

/// <summary>
/// The signed-in supplier's buying-lead inbox (business-flows §6, api.md §2.9):
/// the supplier side of a matched RFQ (<c>RFQMatch</c>, domain-model §5.4). A
/// lead is created by the buyer's submit — the matching pipeline writes one row
/// per selected supplier — and this surface is where the supplier reads it,
/// marks it opened and declines it. The caller's supplier is resolved from
/// their own accepted organisation membership, so one supplier can never read
/// or write another's lead (BOLA, api.md §1.6).
/// <para>
/// What a lead may reveal is fixed by business-flows §6: the RFQ identifier,
/// the project/location context, the requested lines with quantities and units,
/// the respond-by date and the buyer's notes. The buyer's budget, the competing
/// suppliers, their quotes and the buyer's contact details are never part of
/// this surface.
/// </para>
/// </summary>

/// <summary>
/// The inbox views of business-flows §6 (<c>?status=</c>). They are filters over
/// the lead's own lifecycle (§11.2) plus the RFQ's status (§11.1) — never a
/// second status column:
/// <list type="bullet">
/// <item><c>new</c> — the lead has arrived and nothing has been done with it (<c>Matched</c>/<c>Notified</c>).</item>
/// <item><c>due_soon</c> — still answerable and its respond-by date is inside
/// <see cref="SupplierLeadViews.DueSoonWindow"/> (overdue included: it is the
/// most urgent kind of due).</item>
/// <item><c>quoted</c> — the supplier quoted it (<c>Quoting</c>).</item>
/// <item><c>negotiating</c> — the RFQ is in negotiation and this lead was not
/// declined or expired.</item>
/// <item><c>won</c> — the supplier's quote was accepted (§11.3).</item>
/// <item><c>lost</c> — the supplier declined it or it expired.</item>
/// <item><c>follow_up</c> — opened but never answered (<c>Viewed</c>) on an RFQ
/// that is still open.</item>
/// </list>
/// </summary>
public enum SupplierLeadView
{
    New = 0,
    DueSoon = 1,
    Quoted = 2,
    Negotiating = 3,
    Won = 4,
    Lost = 5,
    FollowUp = 6,
}

/// <summary>
/// The lead inbox's constants. business-flows §6 names the <c>due_soon</c> view
/// but not its window; 48 hours is the definition THIS surface uses — one
/// constant, stated so the rule is auditable rather than implied.
/// </summary>
public static class SupplierLeadViews
{
    /// <summary>The wire names of <see cref="SupplierLeadView"/>, in api.md §2.9 order.</summary>
    public static readonly IReadOnlyDictionary<string, SupplierLeadView> ByWireName =
        new Dictionary<string, SupplierLeadView>(StringComparer.Ordinal)
        {
            ["new"] = SupplierLeadView.New,
            ["due_soon"] = SupplierLeadView.DueSoon,
            ["quoted"] = SupplierLeadView.Quoted,
            ["negotiating"] = SupplierLeadView.Negotiating,
            ["won"] = SupplierLeadView.Won,
            ["lost"] = SupplierLeadView.Lost,
            ["follow_up"] = SupplierLeadView.FollowUp,
        };

    /// <summary>How close the respond-by date must be to count as "due soon".</summary>
    public static readonly TimeSpan DueSoonWindow = TimeSpan.FromHours(48);

    /// <summary>Leads per page when the request names no limit.</summary>
    public const int DefaultLimit = 20;

    /// <summary>The largest page a client may ask for (api.md §1 cursor feeds stay bounded).</summary>
    public const int MaxLimit = 100;
}

/// <summary>One row of the inbox (business-flows §6).</summary>
public sealed record SupplierLeadSummaryResponse(
    string LeadId,
    string Status,
    string ReferenceCode,
    string Title,
    string? ReceivedAt,
    string? RespondBy,
    string? RequestedDeliveryAt,
    int LineCount,
    string? ViewedAt,
    string? DeclineReason);

/// <summary>
/// One page of the inbox. Cursor-paginated like every high-volume feed
/// (api.md §1): the cursor encodes <c>(sortValue, publicId)</c> and is opaque
/// to clients.
/// </summary>
public sealed record SupplierLeadPageResponse(
    IReadOnlyList<SupplierLeadSummaryResponse> Items,
    string? NextCursor,
    bool HasMore);

/// <summary>
/// Everything §6 permits a matched supplier to see when it opens a lead. The
/// buyer's internal budget, the competing suppliers, their quotes and the
/// buyer's contact details are deliberately absent (business-flows §6).
/// </summary>
public sealed record SupplierLeadDetailResponse(
    string LeadId,
    string Status,
    string RfqId,
    string ReferenceCode,
    string Title,
    string? DeliveryLocationText,
    string? RequestedDeliveryAt,
    string? RespondBy,
    bool SubstitutionsAllowed,
    string? BuyerNotes,
    IReadOnlyList<SupplierLeadLineResponse> Lines,
    string? NotifiedAt,
    string? ViewedAt,
    string? RespondedAt,
    string? DeclineReason);

/// <summary>
/// One requested line: the requirement to quote against, with its quantity and
/// unit (§6 "requested lines · quantities & units"). <c>lineNumber</c> is the
/// stable reference a quote answers to (§5.3) and <c>rfqItemId</c> is the
/// identifier that reference resolves to — the create-quote contract keys each
/// priced line on it (api.md §2.7), so a client cannot compose a quote without
/// it. The reference's §2.9 example elides the id; exposing it here is what makes
/// the two documented surfaces joinable.
/// </summary>
public sealed record SupplierLeadLineResponse(
    string RfqItemId,
    int LineNumber,
    string Description,
    string? Specification,
    decimal Quantity,
    string? UnitCode,
    string? ExpectedAt,
    string? Notes);

/// <summary>Declining a lead: §11.2's edge requires a reason (max 400, the column's bound).</summary>
public sealed record DeclineSupplierLeadRequest(string Reason);

/// <summary>A lead's lifecycle position and the supplier's own marks on it.</summary>
public static class SupplierLeadStatuses
{
    /// <summary>Statuses a lead can be answered from (nothing has resolved it yet).</summary>
    public static readonly IReadOnlyList<RFQMatchStatus> Answerable =
        [RFQMatchStatus.Matched, RFQMatchStatus.Notified, RFQMatchStatus.Viewed];

    /// <summary>Statuses where the lead is over for the supplier.</summary>
    public static readonly IReadOnlyList<RFQMatchStatus> Terminal =
        [RFQMatchStatus.Declined, RFQMatchStatus.Expired, RFQMatchStatus.Blocked];
}
