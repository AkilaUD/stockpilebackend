using Stockpile.Domain.Entities.Procurement;

namespace Stockpile.Application.Suppliers;

/// <summary>
/// Persistence port for the signed-in supplier's buying-lead inbox
/// (business-flows §6, api.md §2.9), implemented in Infrastructure over
/// <c>RFQMatches</c> joined to <c>RFQs</c>, <c>RFQItems</c> and
/// <c>UnitsOfMeasure</c>. The use case owns identity and the transitions; this
/// port owns the scoped reads and writes, always keyed to a supplier id the
/// caller has already been proven to own — a foreign lead id therefore matches
/// no row and the use case answers the single BOLA 404 (api.md §1.6).
/// </summary>
public interface ISupplierLeadsStore
{
    /// <summary>
    /// One page of the supplier's leads for a view, newest RFQ first
    /// (api.md §1 cursor feed). The port fetches one row beyond the page so
    /// <see cref="SupplierLeadPage.HasMore"/> is a fact, not a guess.
    /// </summary>
    Task<SupplierLeadPage> ListAsync(
        Guid supplierId,
        SupplierLeadView? view,
        SupplierLeadCursorPosition? after,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>The lead as the supplier's own view reads it; null when it is not this supplier's lead.</summary>
    Task<SupplierLeadDetailResponse?> GetAsync(Guid supplierId, Guid leadId, CancellationToken cancellationToken);

    /// <summary>
    /// The tracked lead row for a write, or null when it is not this supplier's
    /// lead. The use case evaluates the §11.2 transition against the row it gets
    /// here and commits through <see cref="SaveChangesAsync"/>.
    /// </summary>
    Task<RfqMatch?> LoadForUpdateAsync(Guid supplierId, Guid leadId, CancellationToken cancellationToken);

    /// <summary>Commits the write (one transaction; the row was loaded by <see cref="LoadForUpdateAsync"/>).</summary>
    Task SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>
/// One inbox row: the wire shape, the lead's public id (the cursor's tiebreak)
/// and the keyset value the cursor is built from (api.md §1).
/// </summary>
public sealed record SupplierLeadRow(SupplierLeadSummaryResponse Lead, Guid LeadId, DateTime SortValue);

/// <summary>A page of <see cref="SupplierLeadRow"/> plus whether another page exists.</summary>
public sealed record SupplierLeadPage(IReadOnlyList<SupplierLeadRow> Rows, bool HasMore);
