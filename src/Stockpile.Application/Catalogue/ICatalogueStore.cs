using Stockpile.Domain.Entities.Catalogue;
using Stockpile.Domain.Entities.Reference;
using Stockpile.Domain.Enums;

namespace Stockpile.Application.Catalogue;

/// <summary>The filters and paging a catalogue listing/search carries (api.md §2.3/§2.5).</summary>
public sealed record CatalogueProductQuery(
    string? CategorySlug,
    string? Q,
    string? Sort,
    Guid? BrandId,
    string? CountryCode,
    bool VerifiedOnly,
    bool? InStockOnly,
    int Page,
    int PageSize);

/// <summary>One typed attribute value for product DTOs.</summary>
public sealed record ProductAttributeProjection(
    string Code,
    string Label,
    string? ValueText,
    decimal? ValueNumber,
    bool? ValueBool,
    AttributeDataType DataType,
    string? UnitCode,
    bool IsFilterable);

/// <summary>Media placement for product DTOs.</summary>
public sealed record ProductMediaProjection(string StorageKey, string? AltText, int SortOrder, bool IsPrimary);

/// <summary>One point on the public price timeline (api.md §3.1).</summary>
public sealed record PriceHistoryProjection(DateTime At, decimal Amount);

/// <summary>
/// The complete read-side picture of one product (headline fields + the child
/// aggregates the public pages render). Price/stock exposure is applied by the
/// service in exact policy terms (domain-model §3.4), never in the renderer.
/// </summary>
public sealed record ProductDetailProjection(
    Guid PublicId,
    string Slug,
    string Name,
    string Sku,
    ItemType ItemType,
    ProductStatus Status,
    Guid SupplierId,
    string SupplierSlug,
    string SupplierName,
    VerificationState SupplierVerification,
    SupplierAudience SupplierAudience,
    SupplierVisibility SupplierVisibility,
    Guid CategoryId,
    string CategorySlug,
    string CategoryName,
    string? Summary,
    string? Description,
    decimal? MinOrderQuantity,
    int? LeadTimeDays,
    string? BrandCode,
    string? BrandName,
    string? CountryOfOriginCode,
    string? CountryOfOriginName,
    PriceState PriceState,
    decimal? PriceAmount,
    string CurrencyCode,
    DateTime? PriceEffectiveAt,
    StockState StockState,
    bool StockTracked,
    int? LowStockThreshold,
    DateTime? MemberSince,
    IReadOnlyList<PriceHistoryProjection> PriceHistory,
    IReadOnlyList<ProductAttributeProjection> Attributes,
    IReadOnlyList<ProductMediaProjection> Media);

/// <summary>Lightweight listing/search projection (subset of the detail projection).</summary>
public sealed record ProductSummaryProjection(
    Guid PublicId,
    string Slug,
    string Name,
    string Sku,
    Guid SupplierId,
    string SupplierSlug,
    string SupplierName,
    bool SupplierVerified,
    string? SupplierDistrict,
    string CategorySlug,
    string CategoryName,
    PriceState PriceState,
    decimal? PriceAmount,
    string CurrencyCode,
    decimal? MinOrderQuantity,
    int? LeadTimeDays,
    ItemType ItemType,
    StockState StockState,
    string? PrimaryMediaStorageKey,
    string? PrimaryMediaAlt,
    IReadOnlyList<ProductAttributeProjection> Attributes);

/// <summary>Supplier directory projection (public subset only, api.md §2.4).</summary>
public sealed record SupplierSummaryProjection(
    Guid PublicId,
    string Slug,
    string Name,
    VerificationState Verification,
    string? District,
    string? Location,
    int? ProductCount,
    string? LogoStorageKey);

/// <summary>One facet option's live count under the active filter.</summary>
public sealed record FacetCount(string Value, string Label, int Count);

/// <summary>
/// Task 2E/2F — the catalogue data port (architecture §8.5 module: catalogue).
/// Read-only today: the seed pipeline and Phase 2B–2E write paths are the only
/// writers, so every method here stays query-shaped with the filters the store
/// can push into SQL (price/visibility policy is applied by the service).
/// </summary>
public interface ICatalogueStore
{
    // ---- Categories ----

    Task<IReadOnlyList<Category>> GetAllCategoriesAsync(CancellationToken cancellationToken);

    Task<Category?> FindCategoryBySlugAsync(string slug, CancellationToken cancellationToken);

    Task<IReadOnlyList<Category>> FindDescendantCategoriesAsync(string materializedPath, CancellationToken cancellationToken);

    /// <summary>Live published-product count per category id (exact; only the listed ids are resolved).</summary>
    Task<IReadOnlyDictionary<Guid, int>> CountPublishedProductsByCategoryAsync(
        IReadOnlyCollection<Guid> categoryIds, CancellationToken cancellationToken);

    // ---- Products ----

    Task<ProductDetailProjection?> FindPublishedProductBySlugAsync(string slug, CancellationToken cancellationToken);

    Task<ProductDetailProjection?> FindPublishedProductByIdAsync(Guid publicId, CancellationToken cancellationToken);

    /// <summary>SqlServer-pushed filtered listing with computed relevance rank when a query is present.</summary>
    Task<IReadOnlyList<ProductSummaryProjection>> QueryProductsAsync(
        CatalogueProductQuery query, CancellationToken cancellationToken);

    Task<int> CountProductsAsync(CatalogueProductQuery query, CancellationToken cancellationToken);

    /// <summary>Products in the same category or the same supplier (excluding self) — the related block (api.md §2.3).</summary>
    Task<IReadOnlyList<ProductSummaryProjection>> GetRelatedProductsAsync(
        Guid productId, int limit, CancellationToken cancellationToken);

    Task<IReadOnlyList<ProductSummaryProjection>> GetSupplierProductsAsync(
        string supplierSlug, int limit, CancellationToken cancellationToken);

    /// <summary>Prefix suggestions over published product names and category names (typeahead).</summary>
    Task<IReadOnlyList<string>> GetSearchSuggestionsAsync(string query, int limit, CancellationToken cancellationToken);

    // ---- Facets (counts under the active filter, api.md §2.5) ----

    Task<IReadOnlyList<FacetCount>> GetOriginFacetCountsAsync(CatalogueProductQuery query, CancellationToken cancellationToken);

    Task<int> GetVerifiedSupplierCountAsync(CatalogueProductQuery query, CancellationToken cancellationToken);

    // ---- Suppliers ----

    Task<IReadOnlyList<SupplierSummaryProjection>> GetSupplierDirectoryAsync(
        string? q, bool? verifiedOnly, CancellationToken cancellationToken);

    Task<Supplier?> FindSupplierBySlugAsync(string slug, CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> GetSupplierCategoryNamesAsync(Guid supplierId, CancellationToken cancellationToken);

    // ---- References ----

    Task<IReadOnlyList<Brand>> GetBrandsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<UnitOfMeasure>> GetUnitsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<AttributeDefinition>> GetAttributeDefinitionsAsync(CancellationToken cancellationToken);

    Task<Country?> FindCountryAsync(string code, CancellationToken cancellationToken);
}