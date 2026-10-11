namespace Stockpile.Contracts.Catalogue;

// ============================================================================
// Public catalogue contracts (api.md §2.3–§2.5, §3.1).
//
// Deliberately spartan: only fields the platform truly holds appear, and every
// "not supplied yet" section is null/absent rather than invented (api.md §3.5
// truthful rendering). Price exposure is settled here once: a supplier's
// audience policy is applied in SQL before a price ever reaches one of these
// DTOs (domain-model §3.4) — a hidden price must never be hidden by CSS.
// ============================================================================

/// <summary>Supplier identity block shared by listings, search and detail.</summary>
public sealed record SupplierRef(
    string PublicId,
    string Slug,
    string Name,
    bool Verified,
    string? District,
    string? Verification);

/// <summary>Category identity block shared by listings and search.</summary>
public sealed record CategoryRef(string Slug, string Name);

/// <summary>One typed attribute value on a product (spec table row).</summary>
public sealed record ProductAttributeValueRef(
    string Code,
    string Label,
    string? Value,
    string? Unit);

/// <summary>First-party media reference; srcset is a single immutable URL today.</summary>
public sealed record ProductImageRef(string? Srcset, string? Alt);

/// <summary>Price exposure (api.md §3.1). Null amount ⇒ OnRequest — amount never reaches the wire for hidden prices.</summary>
public sealed record PriceRef(
    string State,
    decimal? Amount,
    string Currency,
    decimal? MinQuantity,
    string? UnitCode,
    string? UnitName,
    string? TaxState,
    DateTime? EffectiveAt);

/// <summary>Listing / search hit (api.md §2.5 item + §3.1 lightweight form).</summary>
public sealed record ProductListItem(
    string PublicId,
    string Slug,
    string Name,
    string Sku,
    SupplierRef Supplier,
    CategoryRef Category,
    PriceRef? Price,
    string PriceState,
    string StockState,
    decimal? MinOrderQuantity,
    int? LeadTimeDays,
    string ItemType,
    IReadOnlyList<ProductAttributeValueRef> Attributes,
    ProductImageRef? Image);

/// <summary>Facet option for hierarchy/enum/multi facets (§2.5).</summary>
public sealed record FacetOption(string Value, string Label, int Count);

/// <summary>Numeric range facet (type=range, §2.5).</summary>
public sealed record FacetRange(string DataType, string? Unit, decimal Min, decimal Max);

/// <summary>
/// One facet (api.md §2.5). All five types render generically on the frontend —
/// no hard-coded UI per attribute (spec §3.4). Only facets the platform can
/// compute truthfully are emitted.
/// </summary>
public sealed record Facet(
    string Key,
    string Label,
    string Type,
    IReadOnlyList<FacetOption>? Options,
    FacetRange? Range,
    int? Count);

/// <summary>Zero-result bridge to the RFQ path (api.md §2.5).</summary>
public sealed record ZeroResultBridge(string Message, string? RfqPath, CategoryRef? CategorySuggestion);

/// <summary>Search response (api.md §2.5).</summary>
public sealed record SearchResponse(
    string Query,
    int Total,
    int Took,
    string Engine,
    IReadOnlyList<ProductListItem> Items,
    IReadOnlyList<Facet> Facets,
    IReadOnlyList<string> Suggestions,
    ZeroResultBridge? ZeroResultBridge);

/// <summary>Paginated catalogue listing (api.md §2.3 offset pagination, exact totals).</summary>
public sealed record PagedProductResponse(
    IReadOnlyList<ProductListItem> Items,
    int TotalItems,
    int Page,
    int PageSize,
    IReadOnlyList<Facet>? Facets);

/// <summary>Category tree node (api.md §2.3).</summary>
public sealed record CategoryNode(
    string Slug,
    string Name,
    string? NameSi,
    string? ParentSlug,
    int SortOrder,
    bool IsRoot,
    IReadOnlyList<CategoryNode> Children);

/// <summary>Category page payload: the node, its taxonomy preview and first page of products.</summary>
public sealed record CategoryDetailResponse(
    CategoryNode Category,
    IReadOnlyList<ProductListItem> Products,
    int TotalProducts);

/// <summary>Price history entry (api.md §3.1; append-only ledger).</summary>
public sealed record PriceHistoryEntry(DateTime At, decimal Amount);

/// <summary>Stock posture for detail (api.md §3.1).</summary>
public sealed record StockRef(string State, bool Tracked, int? LowStockThreshold);

/// <summary>Brand block for detail (api.md §3.1).</summary>
public sealed record BrandRef(string Code, string Name);

/// <summary>Country-of-origin block (api.md §3.1).</summary>
public sealed record CountryOfOriginRef(string Code, string Name);

/// <summary>Origin block for detail; manufacture country is absent until the supplier supplies it.</summary>
public sealed record OriginRef(CountryOfOriginRef? CountryOfOrigin, CountryOfOriginRef? CountryOfManufacture);

/// <summary>Specification section of the detail page (api.md §3.1).</summary>
public sealed record SpecificationRow(
    string Code,
    string Label,
    string? Value,
    string DataType,
    string? Unit,
    bool IsFilterable);

public sealed record SpecificationSection(string Label, IReadOnlyList<SpecificationRow> Attributes);

public sealed record SpecificationsRef(
    IReadOnlyList<SpecificationSection> Sections,
    int? CompletenessScore);

/// <summary>Why a price is visible or not (api.md §3.1; helpful, never a leak).</summary>
public sealed record VisibilityRef(bool PriceVisible, string? Reason);

/// <summary>Supplier verification inline for detail (api.md §3.1).</summary>
public sealed record SupplierDetailRef(
    string PublicId,
    string Slug,
    string Name,
    string Verification,
    string? District,
    DateTime? MemberSince,
    DateTime? VerifiedAt);

/// <summary>Product detail (api.md §3.1). Sections the supplier did not supply are null, never invented.</summary>
public sealed record ProductDetailResponse(
    string PublicId,
    string Slug,
    string Name,
    string ItemType,
    string Sku,
    string Status,
    SupplierDetailRef Supplier,
    PriceRef? Price,
    IReadOnlyList<PriceHistoryEntry>? PriceHistory,
    StockRef? Stock,
    decimal? MinOrderQuantity,
    int? LeadTimeDays,
    BrandRef? Brand,
    OriginRef? Origin,
    string? Summary,
    string? Description,
    SpecificationsRef? Specifications,
    IReadOnlyList<ProductImageRef> Media,
    VisibilityRef Visibility,
    bool CanAddToRfq,
    IReadOnlyList<ProductListItem>? RelatedProducts,
    IReadOnlyList<SupplierRef>? RelatedSuppliers);

/// <summary>Public category summary feed for the masthead (api.md §2.3 tree shorthand).</summary>
public sealed record CatalogueSummary(int Products, int Suppliers, int Categories);

/// <summary>Brand directory row (api.md §2.3 /catalog/brands).</summary>
public sealed record BrandRefWithCount(BrandRef Brand, int ProductCount);

/// <summary>Unit of measure row (api.md §2.3 /catalog/units).</summary>
public sealed record UnitOfMeasureRef(string Code, string Name, string PluralName, string DimensionKind);

/// <summary>Attribute definition row (api.md §2.3 /catalog/attributes).</summary>
public sealed record AttributeRef(
    string Code,
    string DisplayName,
    string DataType,
    string? UnitCode,
    bool IsFilterable,
    IReadOnlyList<FacetOption>? Options);

/// <summary>Supplier directory row (api.md §2.4). Public data only — internal contacts never appear.</summary>
public sealed record SupplierDirectoryItem(
    string PublicId,
    string Slug,
    string Name,
    bool Verified,
    string? District,
    string? Location,
    int? ProductCount,
    ProductImageRef? Logo);

/// <summary>Supplier directory page (api.md §2.4, ?view=list).</summary>
public sealed record SupplierDirectoryResponse(
    IReadOnlyList<SupplierDirectoryItem> Items,
    int TotalItems);

/// <summary>Supplier microsite payload (api.md §2.4) — the public subset only.</summary>
public sealed record SupplierDetailResponse(
    string PublicId,
    string Slug,
    string Name,
    string Verification,
    bool Verified,
    string? Story,
    string? District,
    string? Location,
    string? ContactPerson,
    string? Phone,
    string? Email,
    string? Website,
    int? ProductCount,
    IReadOnlyList<string> Categories,
    IReadOnlyList<ProductListItem>? FeaturedProducts,
    ProductImageRef? Logo,
    ProductImageRef? Banner);

/// <summary>Command-search overlay payload (api.md §2.3 /search/command).</summary>
public sealed record CommandSearchResponse(
    string Query,
    IReadOnlyList<ProductListItem> Products,
    IReadOnlyList<CategoryNode> Categories,
    IReadOnlyList<SupplierDirectoryItem> Suppliers);