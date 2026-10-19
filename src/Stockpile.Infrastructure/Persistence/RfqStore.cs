using Microsoft.EntityFrameworkCore;
using Stockpile.Application.Procurement;
using Stockpile.Domain.Entities.Catalogue;
using Stockpile.Domain.Entities.Procurement;
using Stockpile.Domain.Enums;

namespace Stockpile.Infrastructure.Persistence;

/// <summary>
/// EF Core implementation of the RFQ port (architecture §8.5 module
/// "procurement"). Writes are single <c>SaveChanges</c> calls so the aggregate,
/// its matches and its status history always commit together; reads compose the
/// reference values (category slug, unit code, supplier identity) the response
/// needs so the Application layer never re-joins.
/// </summary>
public sealed class RfqStore(StockpileDbContext db) : IRfqStore
{
    /// <inheritdoc />
    public async Task<Guid?> FindActiveCategoryIdBySlugAsync(string slug, CancellationToken cancellationToken)
        => await db.Categories
            .Where(category => category.Slug == slug && category.IsActive)
            .Select(category => (Guid?)category.PublicId)
            .FirstOrDefaultAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<Guid?> FindUnitOfMeasureIdByCodeAsync(string code, CancellationToken cancellationToken)
        => await db.UnitOfMeasures
            .Where(unit => unit.Code == code)
            .Select(unit => (Guid?)unit.PublicId)
            .FirstOrDefaultAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<Guid>> ExpandToAncestorsAsync(
        IReadOnlyCollection<Guid> categoryIds, CancellationToken cancellationToken)
    {
        // The category tree is small and read whole; walking parents in memory
        // beats a recursive CTE for this fan-out (§3.8 step 5).
        Dictionary<Guid, Guid?> parents = await db.Categories
            .Select(category => new { category.PublicId, category.ParentId })
            .ToDictionaryAsync(category => category.PublicId, category => category.ParentId, cancellationToken);

        HashSet<Guid> expanded = [.. categoryIds];
        Queue<Guid> pending = new(categoryIds);
        while (pending.Count > 0)
        {
            Guid current = pending.Dequeue();
            if (parents.TryGetValue(current, out Guid? parent) && parent is Guid parentId && expanded.Add(parentId))
            {
                pending.Enqueue(parentId);
            }
        }

        return [.. expanded];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SupplierMatch>> FindQuotableSuppliersAsync(
        IReadOnlyCollection<Guid> categoryIds, CancellationToken cancellationToken)
    {
        var rows = await (
            from coverage in db.SupplierCategories
            join supplier in db.Suppliers on coverage.SupplierId equals supplier.PublicId
            join category in db.Categories on coverage.CategoryId equals category.PublicId
            where coverage.Quotable
                && categoryIds.Contains(coverage.CategoryId)
                && supplier.Status == SupplierStatus.Active
            select new
            {
                SupplierId = supplier.PublicId,
                supplier.DisplayName,
                SupplierSlug = supplier.Slug,
                CategorySlug = category.Slug,
            }).ToListAsync(cancellationToken);

        // Match count is data-driven (§3.8): one row per supplier, scored by the
        // number of requested departments they cover, then capped so a broad
        // request cannot dispatch an unbounded fan-out.
        return rows
            .GroupBy(row => new { row.SupplierId, row.DisplayName, row.SupplierSlug })
            .Select(group => new SupplierMatch(
                group.Key.SupplierId,
                group.Key.DisplayName,
                group.Key.SupplierSlug,
                group.Select(row => row.CategorySlug).Distinct(StringComparer.Ordinal).ToList()))
            .OrderByDescending(match => match.MatchedCategorySlugs.Count)
            .ThenBy(match => match.DisplayName, StringComparer.Ordinal)
            .Take(50)
            .ToList();
    }

    /// <inheritdoc />
    public Task<bool> ReferenceCodeExistsAsync(string referenceCode, CancellationToken cancellationToken)
        => db.Rfqs.AnyAsync(rfq => rfq.ReferenceCode == referenceCode, cancellationToken);

    /// <inheritdoc />
    public async Task AddAsync(Rfq rfq, IReadOnlyCollection<RfqItem> items, CancellationToken cancellationToken)
    {
        db.Rfqs.Add(rfq);
        db.RfqItems.AddRange(items);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task SaveSubmissionAsync(
        Rfq rfq,
        IReadOnlyCollection<RfqMatch> matches,
        IReadOnlyCollection<RfqStatusHistory> history,
        CancellationToken cancellationToken)
    {
        db.Rfqs.Update(rfq); // loaded no-tracking; the carried RowVersion guards the write
        db.RfqMatches.AddRange(matches);
        db.RfqStatusHistories.AddRange(history);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<RfqDetail?> LoadAsync(Guid rfqId, CancellationToken cancellationToken)
    {
        IReadOnlyList<RfqDetail> details = await LoadManyAsync([rfqId], cancellationToken);
        return details.Count == 0 ? null : details[0];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RfqDetail>> LoadManyAsync(
        IReadOnlyCollection<Guid> rfqIds, CancellationToken cancellationToken)
    {
        List<Rfq> rfqs = await db.Rfqs
            .AsNoTracking()
            .Where(rfq => rfqIds.Contains(rfq.PublicId))
            .OrderByDescending(rfq => rfq.CreatedAt)
            .ToListAsync(cancellationToken);
        if (rfqs.Count == 0)
        {
            return [];
        }

        List<Guid> ids = rfqs.Select(rfq => rfq.PublicId).ToList();
        List<RfqItem> items = await db.RfqItems
            .AsNoTracking()
            .Where(item => ids.Contains(item.RfqId))
            .ToListAsync(cancellationToken);
        List<RfqMatch> matches = await db.RfqMatches
            .AsNoTracking()
            .Where(match => ids.Contains(match.RfqId))
            .ToListAsync(cancellationToken);

        List<Guid> categoryIds = items
            .Where(item => item.CategoryId.HasValue).Select(item => item.CategoryId!.Value).Distinct().ToList();
        List<Guid> unitIds = items
            .Where(item => item.UnitOfMeasureId.HasValue).Select(item => item.UnitOfMeasureId!.Value).Distinct().ToList();
        List<Guid> supplierIds = matches.Select(match => match.SupplierId).Distinct().ToList();

        Dictionary<Guid, string> categorySlugs = categoryIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.Categories.Where(category => categoryIds.Contains(category.PublicId))
                .ToDictionaryAsync(category => category.PublicId, category => category.Slug, cancellationToken);
        Dictionary<Guid, string> unitCodes = unitIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.UnitOfMeasures.Where(unit => unitIds.Contains(unit.PublicId))
                .ToDictionaryAsync(unit => unit.PublicId, unit => unit.Code, cancellationToken);
        Dictionary<Guid, Supplier> suppliers = supplierIds.Count == 0
            ? new Dictionary<Guid, Supplier>()
            : await db.Suppliers.Where(supplier => supplierIds.Contains(supplier.PublicId))
                .ToDictionaryAsync(supplier => supplier.PublicId, cancellationToken);

        return rfqs.Select(rfq => new RfqDetail(
            rfq,
            items.Where(item => item.RfqId == rfq.PublicId)
                .OrderBy(item => item.LineNumber)
                .Select(item => new RfqItemDetail(
                    item,
                    item.CategoryId is Guid categoryId && categorySlugs.TryGetValue(categoryId, out string? slug)
                        ? slug
                        : null,
                    item.UnitOfMeasureId is Guid unitId && unitCodes.TryGetValue(unitId, out string? code)
                        ? code
                        : null))
                .ToList(),
            matches.Where(match => match.RfqId == rfq.PublicId)
                .Select(match =>
                {
                    suppliers.TryGetValue(match.SupplierId, out Supplier? supplier);
                    return new RfqMatchDetail(
                        match, supplier?.DisplayName ?? "Supplier", supplier?.Slug ?? string.Empty);
                })
                .ToList()))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Guid>> GetOrganizationRfqIdsAsync(
        IReadOnlyCollection<Guid> organizationIds, CancellationToken cancellationToken)
        => await db.Rfqs
            .Where(rfq => organizationIds.Contains(rfq.OrganizationId))
            .OrderByDescending(rfq => rfq.CreatedAt)
            .Select(rfq => rfq.PublicId)
            .ToListAsync(cancellationToken);
}
