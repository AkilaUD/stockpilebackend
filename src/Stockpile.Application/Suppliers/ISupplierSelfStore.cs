namespace Stockpile.Application.Suppliers;

/// <summary>
/// Persistence port for the signed-in supplier's own surface (api.md §2.9),
/// implemented in Infrastructure over <c>OrganizationMembers</c>,
/// <c>Organizations</c>, <c>Suppliers</c>, <c>SupplierProfiles</c>,
/// <c>SupplierCategories</c>, <c>Categories</c>, <c>SupplierServiceAreas</c> and
/// the reference <c>Districts</c> table (domain-model §3.1/§3.3). The use case
/// owns identity and orchestration; this port owns the scoped reads and writes,
/// always keyed to a supplier id the caller has already been proven to own.
/// </summary>
public interface ISupplierSelfStore
{
    /// <summary>The caller's own supplier, resolved through their accepted membership; null when they have none.</summary>
    Task<Guid?> ResolveSupplierIdAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>The supplier aggregate as the onboarding surface reads it; null when the id no longer resolves.</summary>
    Task<SupplierSelfResponse?> GetSelfAsync(Guid supplierId, CancellationToken cancellationToken);

    /// <summary>Merges stages 2–3 and returns the stored profile; null when the supplier no longer resolves.</summary>
    Task<SupplierProfileResponse?> UpdateProfileAsync(
        Guid supplierId, SupplierProfileDraft draft, CancellationToken cancellationToken);

    /// <summary>Resolves a category slug to its id, or null when the taxonomy does not know it (or it is inactive).</summary>
    Task<Guid?> ResolveCategoryIdAsync(string slug, CancellationToken cancellationToken);

    /// <summary>Adds coverage (idempotent) and returns the supplier's coverage; null when the supplier no longer resolves.</summary>
    Task<IReadOnlyList<SupplierCategoryResponse>?> AddCategoryAsync(
        Guid supplierId, Guid categoryId, CancellationToken cancellationToken);

    /// <summary>Removes coverage; false when the supplier did not cover that category.</summary>
    Task<bool> RemoveCategoryAsync(Guid supplierId, string slug, CancellationToken cancellationToken);

    /// <summary>True when every code names a real district.</summary>
    Task<bool> AllDistrictsExistAsync(IReadOnlyList<string> districtCodes, CancellationToken cancellationToken);

    /// <summary>Replaces the supplier's service areas (stage 4) and returns them; null when the supplier no longer resolves.</summary>
    Task<IReadOnlyList<SupplierServiceAreaResponse>?> SetServiceAreasAsync(
        Guid supplierId, IReadOnlyList<string> districtCodes, string? defaultDistrict, CancellationToken cancellationToken);
}
