using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Stockpile.Application.Suppliers;
using Stockpile.Domain.Entities.Catalogue;
using Stockpile.Domain.Entities.Procurement;
using Stockpile.Domain.Enums;

namespace Stockpile.Infrastructure.Persistence.Stores;

/// <summary>
/// EF implementation of <see cref="ISupplierLeadsStore"/> (business-flows §6,
/// api.md §2.9). Every query is keyed to a supplier id the caller has already
/// been proven to own (resolved from their accepted organisation membership), so
/// one supplier can never read or write another's lead (BOLA, api.md §1.6).
/// <para>
/// The feed's keyset position is the RFQ's arrival — its submission time, or its
/// creation time for a row that predates submission — with the lead's public id
/// as the tiebreak (api.md §1 "cursor encodes (sortValue, publicId)"). The
/// tiebreak comparison and the ordering it breaks are both evaluated by SQL
/// Server over the same <c>uniqueidentifier</c> column, which is what keeps the
/// two pages of a tie from overlapping or skipping.
/// </para>
/// </summary>
public sealed class SupplierLeadsStore(StockpileDbContext db) : ISupplierLeadsStore
{
    /// <inheritdoc />
    public async Task<SupplierLeadPage> ListAsync(
        Guid supplierId,
        SupplierLeadView? view,
        SupplierLeadCursorPosition? after,
        int limit,
        CancellationToken cancellationToken)
    {
        DateTime now = DateTime.UtcNow;

        // A member-init over SCALARS (never entity references) — the shape EF
        // translates unconditionally, with the line count as the one correlated
        // subquery.
        IQueryable<LeadRow> query =
            from match in db.RfqMatches.AsNoTracking()
            join rfq in db.Rfqs.AsNoTracking() on match.RfqId equals rfq.PublicId
            where match.SupplierId == supplierId
            select new LeadRow
            {
                MatchId = match.PublicId,
                Status = match.Status,
                ViewedAt = match.ViewedAt,
                DeclineReason = match.DeclineReason,
                MatchExpiryAt = match.ExpiryAt,
                ReferenceCode = rfq.ReferenceCode,
                Title = rfq.Title,
                RfqExpiryAt = rfq.ExpiryAt,
                RequestedDeliveryAt = rfq.RequestedDeliveryAt,
                RfqStatus = rfq.Status,
                SortValue = rfq.SubmittedAt ?? rfq.CreatedAt,
                LineCount = db.RfqItems.Count(item => item.RfqId == rfq.PublicId),
            };

        query = ApplyView(query, view, now);

        if (after is not null)
        {
            query = query.Where(row => row.SortValue < after.SortValue
                || (row.SortValue == after.SortValue && row.MatchId > after.LeadId));
        }

        List<LeadRow> rows = await query
            .OrderByDescending(row => row.SortValue)
            .ThenBy(row => row.MatchId)
            .Take(limit + 1) // one beyond the page, so HasMore is a fact rather than a guess
            .ToListAsync(cancellationToken);

        bool hasMore = rows.Count > limit;
        return new SupplierLeadPage(rows.Take(limit).Select(ToRow).ToList(), hasMore);
    }

    /// <inheritdoc />
    public async Task<SupplierLeadDetailResponse?> GetAsync(
        Guid supplierId, Guid leadId, CancellationToken cancellationToken)
    {
        RfqMatch? match = await db.RfqMatches
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.SupplierId == supplierId && row.PublicId == leadId, cancellationToken);
        if (match is null)
        {
            return null;
        }

        Rfq? rfq = await db.Rfqs
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.PublicId == match.RfqId, cancellationToken);
        if (rfq is null)
        {
            return null;
        }

        List<RfqItem> items = await db.RfqItems
            .AsNoTracking()
            .Where(item => item.RfqId == rfq.PublicId)
            .OrderBy(item => item.LineNumber)
            .ToListAsync(cancellationToken);
        Dictionary<Guid, string> unitCodes = await UnitCodesAsync(items, cancellationToken);

        return ToDetail(match, rfq, items, unitCodes);
    }

    /// <inheritdoc />
    public async Task<RfqMatch?> LoadForUpdateAsync(
        Guid supplierId, Guid leadId, CancellationToken cancellationToken)
        => await db.RfqMatches
            .FirstOrDefaultAsync(row => row.SupplierId == supplierId && row.PublicId == leadId, cancellationToken);

    /// <inheritdoc />
    public async Task SaveChangesAsync(CancellationToken cancellationToken)
        => await db.SaveChangesAsync(cancellationToken);

    // ---- Internals ----------------------------------------------------------

    /// <summary>
    /// The view predicates (business-flows §6). Each one is expressed over data
    /// the lead already has — its own §11.2 status, the RFQ's §11.1 status and
    /// the respond-by date — so a view is a filter, never a second status column.
    /// </summary>
    private static IQueryable<LeadRow> ApplyView(
        IQueryable<LeadRow> query, SupplierLeadView? view, DateTime now)
        => view switch
        {
            null => query,

            // "new (Matched/Notified)" — transcribed from business-flows §6. A
            // blocked lead is deliberately not new: an operator paused it.
            SupplierLeadView.New => query.Where(row =>
                row.Status == RFQMatchStatus.Matched || row.Status == RFQMatchStatus.Notified),

            // "due_soon": still answerable and its respond-by date is inside the
            // window — overdue included, which is the most urgent kind of due.
            SupplierLeadView.DueSoon => query.Where(row =>
                (row.Status == RFQMatchStatus.Matched
                    || row.Status == RFQMatchStatus.Notified
                    || row.Status == RFQMatchStatus.Viewed)
                && row.MatchExpiryAt != null
                && row.MatchExpiryAt <= now.Add(SupplierLeadViews.DueSoonWindow)),

            // "quoted (Quoting)" — transcribed from business-flows §6. The lead
            // reaches Quoting when the supplier submits a quote (§11.2), so this
            // view is empty until the quote surface lands.
            SupplierLeadView.Quoted => query.Where(row => row.Status == RFQMatchStatus.Quoting),

            // The RFQ is in negotiation (§11.1) and this lead was not resolved.
            SupplierLeadView.Negotiating => query.Where(row =>
                row.RfqStatus == RFQStatus.Negotiating
                && row.Status != RFQMatchStatus.Declined
                && row.Status != RFQMatchStatus.Expired
                && row.Status != RFQMatchStatus.Blocked),

            // "won": the supplier's quote was ACCEPTED (§11.3). Acceptance is what
            // makes a win — the RFQ's own Awarded status cannot say WHICH supplier
            // won, so it is never used as the win signal. Nothing can be won before
            // a quote exists, so the view answers empty until the quote surface
            // lands; a false "won" is worse than an empty one.
            SupplierLeadView.Won => query.Where(row => false),

            // "lost (Declined/Expired)" — transcribed from business-flows §6.
            SupplierLeadView.Lost => query.Where(row =>
                row.Status == RFQMatchStatus.Declined || row.Status == RFQMatchStatus.Expired),

            // Opened but never answered, on an RFQ that is still open. Terminal
            // RFQ states (§11.1) leave nothing to follow up on.
            SupplierLeadView.FollowUp => query.Where(row =>
                row.Status == RFQMatchStatus.Viewed
                && row.RfqStatus != RFQStatus.Cancelled
                && row.RfqStatus != RFQStatus.Closed
                && row.RfqStatus != RFQStatus.Expired
                && row.RfqStatus != RFQStatus.Awarded),

            _ => query,
        };

    private async Task<Dictionary<Guid, string>> UnitCodesAsync(
        IReadOnlyCollection<RfqItem> items, CancellationToken cancellationToken)
    {
        List<Guid> unitIds = items
            .Where(item => item.UnitOfMeasureId.HasValue)
            .Select(item => item.UnitOfMeasureId!.Value)
            .Distinct()
            .ToList();

        return unitIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.UnitOfMeasures
                .Where(unit => unitIds.Contains(unit.PublicId))
                .ToDictionaryAsync(unit => unit.PublicId, unit => unit.Code, cancellationToken);
    }

    private static SupplierLeadRow ToRow(LeadRow row) => new(
        new SupplierLeadSummaryResponse(
            row.MatchId.ToString(),
            row.Status.ToString(),
            row.ReferenceCode,
            row.Title,
            Iso(row.SortValue),
            Iso(row.MatchExpiryAt ?? row.RfqExpiryAt),
            Iso(row.RequestedDeliveryAt),
            row.LineCount,
            Iso(row.ViewedAt),
            row.DeclineReason),
        row.MatchId,
        row.SortValue);

    private static SupplierLeadDetailResponse ToDetail(
        RfqMatch match, Rfq rfq, IReadOnlyList<RfqItem> items, Dictionary<Guid, string> unitCodes)
        => new(
            match.PublicId.ToString(),
            match.Status.ToString(),
            rfq.PublicId.ToString(),
            rfq.ReferenceCode,
            rfq.Title,
            rfq.DeliveryLocationText,
            Iso(rfq.RequestedDeliveryAt),
            Iso(RowExpiry(match, rfq)),
            rfq.SubstitutionsAllowed,
            rfq.SpecialInstructions,
            items
                .Select(item => new SupplierLeadLineResponse(
                    item.PublicId.ToString(),
                    item.LineNumber,
                    item.Description,
                    item.Specification,
                    item.Quantity,
                    item.UnitOfMeasureId is Guid unitId && unitCodes.TryGetValue(unitId, out string? code)
                        ? code
                        : null,
                    Iso(item.ExpectedAt),
                    item.Notes))
                .ToList(),
            Iso(match.NotifiedAt),
            Iso(match.ViewedAt),
            Iso(match.RespondedAt),
            match.DeclineReason);

    /// <summary>The lead's respond-by date: its own, falling back to the RFQ's (§5.4).</summary>
    private static DateTime? RowExpiry(RfqMatch match, Rfq rfq) => match.ExpiryAt ?? rfq.ExpiryAt;

    /// <summary>
    /// The columns are UTC by convention (domain-model §1) and <c>datetime2</c>
    /// carries no kind, so the value is stamped UTC before it is written — the
    /// wire instant is unambiguous instead of one a client has to guess.
    /// </summary>
    private static string? Iso(DateTime? value) =>
        value is null
            ? null
            : DateTime.SpecifyKind(value.Value, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture);

    private static string Iso(DateTime value) =>
        DateTime.SpecifyKind(value, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture);

    /// <summary>
    /// A feed row before it becomes a wire shape: everything the feed's filters,
    /// keyset and wire shape need from the two joined rows — held as SCALARS,
    /// never entity references, which is what keeps the whole query translatable.
    /// </summary>
    private sealed class LeadRow
    {
        public Guid MatchId { get; set; }

        public RFQMatchStatus Status { get; set; }

        public DateTime? ViewedAt { get; set; }

        public string? DeclineReason { get; set; }

        public DateTime? MatchExpiryAt { get; set; }

        public string ReferenceCode { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;

        public DateTime? RfqExpiryAt { get; set; }

        public DateTime? RequestedDeliveryAt { get; set; }

        public RFQStatus RfqStatus { get; set; }

        public DateTime SortValue { get; set; }

        public int LineCount { get; set; }
    }
}
