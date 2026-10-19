using Stockpile.Domain.Entities.Procurement;

namespace Stockpile.Application.Procurement;

/// <summary>
/// RFQ data port (architecture §8.5 module "procurement"). Writes stay
/// transaction-shaped on the store side; the Application layer owns the rules
/// (matching, transitions, organisation resolution). Reads return composed
/// details so the service never re-joins reference data.
/// </summary>
public interface IRfqStore
{
    /// <summary>The public id of an ACTIVE category with this slug, or null.</summary>
    Task<Guid?> FindActiveCategoryIdBySlugAsync(string slug, CancellationToken cancellationToken);

    /// <summary>The public id of the unit of measure with this code, or null.</summary>
    Task<Guid?> FindUnitOfMeasureIdByCodeAsync(string code, CancellationToken cancellationToken);

    /// <summary>Expands a set of category ids to include every ancestor (RFQ matching walks the tree up, §3.8).</summary>
    Task<IReadOnlyList<Guid>> ExpandToAncestorsAsync(
        IReadOnlyCollection<Guid> categoryIds, CancellationToken cancellationToken);

    /// <summary>Active, quotable suppliers covering any of the categories, with the overlapping category slugs.</summary>
    Task<IReadOnlyList<SupplierMatch>> FindQuotableSuppliersAsync(
        IReadOnlyCollection<Guid> categoryIds, CancellationToken cancellationToken);

    Task<bool> ReferenceCodeExistsAsync(string referenceCode, CancellationToken cancellationToken);

    /// <summary>Persist a new draft RFQ and its items in one SaveChanges (one transaction).</summary>
    Task AddAsync(Rfq rfq, IReadOnlyCollection<RfqItem> items, CancellationToken cancellationToken);

    /// <summary>Persist a submit: the transitioned RFQ, its matches and its status history together.</summary>
    Task SaveSubmissionAsync(
        Rfq rfq,
        IReadOnlyCollection<RfqMatch> matches,
        IReadOnlyCollection<RfqStatusHistory> history,
        CancellationToken cancellationToken);

    Task<RfqDetail?> LoadAsync(Guid rfqId, CancellationToken cancellationToken);

    Task<IReadOnlyList<RfqDetail>> LoadManyAsync(IReadOnlyCollection<Guid> rfqIds, CancellationToken cancellationToken);

    /// <summary>The RFQ ids owned by any of these organisations, newest first.</summary>
    Task<IReadOnlyList<Guid>> GetOrganizationRfqIdsAsync(
        IReadOnlyCollection<Guid> organizationIds, CancellationToken cancellationToken);
}

/// <summary>An RFQ with its items and matches, plus the reference values their responses need.</summary>
public sealed record RfqDetail(
    Rfq Rfq,
    IReadOnlyList<RfqItemDetail> Items,
    IReadOnlyList<RfqMatchDetail> Matches);

/// <summary>One item with its resolved category slug and unit code (null when the item named none).</summary>
public sealed record RfqItemDetail(RfqItem Item, string? CategorySlug, string? UnitCode);

/// <summary>One match with the supplier's display name and slug for the buyer's view.</summary>
public sealed record RfqMatchDetail(RfqMatch Match, string SupplierName, string SupplierSlug);

/// <summary>The matching pipeline's output for one supplier (transparent rule evidence, §3.8 step 8).</summary>
public sealed record SupplierMatch(
    Guid SupplierId,
    string DisplayName,
    string Slug,
    IReadOnlyList<string> MatchedCategorySlugs);
