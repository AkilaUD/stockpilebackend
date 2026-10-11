using System.Diagnostics;
using Stockpile.Application.Catalogue;
using Stockpile.Application.Common.Errors;
using Stockpile.Contracts.Catalogue;
using Stockpile.Contracts.Errors;
using Stockpile.Domain.Entities.Catalogue;
using Stockpile.Domain.Enums;

namespace Stockpile.Application.Catalogue;

/// <summary>
/// Task 2E — the public catalogue use cases (api.md §2.3–§2.5, §3.1). Read-only
/// projection work: the store pushes filters into SQL, the service owns
/// audience-policy exposure (domain-model §3.4) and DTO shaping. Every response
/// is either real data or an explicit null — nothing is invented to fill a
/// missing section (api.md §3.5).
/// </summary>
public sealed class CatalogueService(ICatalogueStore store)
{
    private const string Engine = "relational";
    private const int DefaultPageSize = 48;
    private const int MaxPageSize = 96;

    // ------------------------------------------------------------------ home

    /// <summary>The masthead pulse numbers (§8.1/§8.3): live counts, exact totals.</summary>
    public async Task<CatalogueSummary> GetSummaryAsync(CancellationToken cancellationToken)
    {
        var categories = await store.GetAllCategoriesAsync(cancellationToken);
        var products = await store.CountProductsAsync(
            new CatalogueProductQuery(null, null, null, null, null, false, null, 0, int.MaxValue), cancellationToken);
        var suppliers = await store.GetSupplierDirectoryAsync(null, null, cancellationToken);
        return new CatalogueSummary(
            Products: products,
            Suppliers: suppliers.Count,
            Categories: categories.Count(c => c.IsActive));
    }

    // ------------------------------------------------------------- categories

    public async Task<IReadOnlyList<CategoryNode>> GetCategoryTreeAsync(CancellationToken cancellationToken)
    {
        var all = await store.GetAllCategoriesAsync(cancellationToken);
        var byPublicId = all.ToDictionary(c => c.PublicId);
        var childrenByParent = all
            .Where(c => c.ParentId is not null)
            .GroupBy(c => c.ParentId!.Value)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ToList());

        // Build recursively from the roots: assigning Children with `with`
        // while iterating captured stale instances (a parent grabbed its child
        // before the child gained its own subtree), silently truncating deep
        // branches of the returned tree.
        CategoryNode Build(Category category) => new(
            category.Slug,
            category.Name,
            category.NameSi,
            category.ParentId is { } parentId && byPublicId.TryGetValue(parentId, out var parent) ? parent.Slug : null,
            category.SortOrder,
            category.ParentId is null,
            childrenByParent.TryGetValue(category.PublicId, out var kids)
                ? kids.Select(Build).ToList()
                : []);

        return all
            .Where(c => c.ParentId is null)
            .OrderBy(c => c.SortOrder)
            .ThenBy(c => c.Name)
            .Select(Build)
            .ToList();
    }

    public async Task<CategoryDetailResponse> GetCategoryDetailAsync(
        string slug, int page, int pageSize, CancellationToken cancellationToken)
    {
        var category = await store.FindCategoryBySlugAsync(slug, cancellationToken)
            ?? throw new AppException(ErrorCodes.ResourceNotFound);
        if (!category.IsActive)
            throw new AppException(ErrorCodes.ResourceNotFound);

        var size = ClampPageSize(pageSize);
        var number = clampedPage(page, size);
        var query = new CatalogueProductQuery(category.Slug, null, "newest", null, null, false, null, number, size);
        var products = await store.QueryProductsAsync(query, cancellationToken);
        var total = await store.CountProductsAsync(query, cancellationToken);

        var all = await store.GetAllCategoriesAsync(cancellationToken);
        var node = BuildNode(category, all);
        return new CategoryDetailResponse(node, products.Select(MapListItem).ToList(), total);
    }

    // --------------------------------------------------------------- products

    public async Task<PagedProductResponse> GetProductsAsync(
        string? categorySlug, string? q, string? sort, Guid? brandId, string? countryCode,
        bool verifiedOnly, bool? inStockOnly, int page, int pageSize, CancellationToken cancellationToken)
    {
        var size = ClampPageSize(pageSize);
        var number = clampedPage(page, size);
        var query = new CatalogueProductQuery(categorySlug, q, sort, brandId, countryCode, verifiedOnly, inStockOnly, number, size);
        var products = await store.QueryProductsAsync(query, cancellationToken);
        var total = await store.CountProductsAsync(query, cancellationToken);
        var facets = await BuildFacetsAsync(query, cancellationToken);
        return new PagedProductResponse(products.Select(MapListItem).ToList(), total, number, size, facets);
    }

    public async Task<ProductDetailResponse> GetProductBySlugAsync(string slug, CancellationToken cancellationToken)
    {
        var p = await store.FindPublishedProductBySlugAsync(slug, cancellationToken)
            ?? throw new AppException(ErrorCodes.ResourceNotFound);

        var related = await store.GetRelatedProductsAsync(p.PublicId, 8, cancellationToken);
        return MapDetail(p, related);
    }

    /// <summary>Related block by public id (api.md §2.3 <c>products/{id}/related</c>).</summary>
    public async Task<IReadOnlyList<ProductListItem>> GetRelatedProductsAsync(
        Guid publicId, CancellationToken cancellationToken)
    {
        var detail = await store.FindPublishedProductByIdAsync(publicId, cancellationToken)
            ?? throw new AppException(ErrorCodes.ResourceNotFound);
        var related = await store.GetRelatedProductsAsync(publicId, 8, cancellationToken);
        return related.Select(MapListItem).ToList();
    }

    public async Task<PagedProductResponse> GetSupplierProductsAsync(
        string slug, int page, int pageSize, CancellationToken cancellationToken)
    {
        var supplier = await store.FindSupplierBySlugAsync(slug, cancellationToken)
            ?? throw new AppException(ErrorCodes.ResourceNotFound);
        if (supplier.Status != SupplierStatus.Active)
            throw new AppException(ErrorCodes.ResourceNotFound);

        var size = ClampPageSize(pageSize);
        var number = clampedPage(page, size);
        var query = new CatalogueProductQuery(null, null, "newest", null, null, false, null, number, size);
        var products = await store.GetSupplierProductsAsync(slug, 10_000, cancellationToken);
        var pageSlice = products.Skip((number - 1) * size).Take(size).ToList();
        return new PagedProductResponse(pageSlice.Select(MapListItem).ToList(), products.Count, number, size, null);
    }

    // ----------------------------------------------------------------- search

    public async Task<SearchResponse> SearchAsync(
        string query, string? categorySlug, string? sort, Guid? brandId, string? countryCode,
        bool verifiedOnly, int page, int pageSize, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var size = ClampPageSize(pageSize);
        var number = clampedPage(page, size);
        var trimmed = query.Trim();
        var q = new CatalogueProductQuery(categorySlug, trimmed, sort ?? "relevance", brandId, countryCode, verifiedOnly, null, number, size);
        var products = await store.QueryProductsAsync(q, cancellationToken);
        var total = await store.CountProductsAsync(q, cancellationToken);
        var facets = await BuildFacetsAsync(q, cancellationToken);
        var suggestions = total == 0
            ? await store.GetSearchSuggestionsAsync(trimmed, 4, cancellationToken)
            : [];
        sw.Stop();

        CategoryRef? suggestion = null;
        if (total == 0 && trimmed.Length > 0)
        {
            var all = await store.GetAllCategoriesAsync(cancellationToken);
            var best = all
                .FirstOrDefault(c =>
                    c.IsActive && c.Slug.Contains(trimmed, StringComparison.OrdinalIgnoreCase));
            if (best is not null) suggestion = new CategoryRef(best.Slug, best.Name);
        }

        var bridge = total == 0
            ? new ZeroResultBridge(
                Message: "No listed product matches this specification.",
                RfqPath: null,
                CategorySuggestion: suggestion)
            : null;

        return new SearchResponse(
            trimmed, total, (int)sw.ElapsedMilliseconds, Engine,
            products.Select(MapListItem).ToList(), facets, suggestions, bridge);
    }

    /// <summary>Masthead typeahead: prefix matches over product names and category names.</summary>
    public async Task<IReadOnlyList<string>> GetSuggestionsAsync(string query, CancellationToken cancellationToken)
        => query.Trim().Length < 2
            ? []
            : await store.GetSearchSuggestionsAsync(query.Trim(), 8, cancellationToken);

    /// <summary>Command-search overlay (api.md §2.3): grouped top hits across products, categories, suppliers.</summary>
    public async Task<CommandSearchResponse> CommandSearchAsync(string query, CancellationToken cancellationToken)
    {
        var q = query.Trim();
        if (q.Length < 2)
            return new CommandSearchResponse(q, [], [], []);

        var products = await store.QueryProductsAsync(
            new CatalogueProductQuery(null, q, "relevance", null, null, false, null, 1, 5), cancellationToken);
        var categories = await store.GetAllCategoriesAsync(cancellationToken);
        var topCategories = categories
            .Where(c => c.IsActive && c.Slug.Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderBy(c => c.SortOrder)
            .Take(4)
            .Select(c => new CategoryNode(c.Slug, c.Name, c.NameSi, null, c.SortOrder, c.ParentId is null, []))
            .ToList();
        var suppliers = await store.GetSupplierDirectoryAsync(q, null, cancellationToken);
        return new CommandSearchResponse(
            q,
            products.Select(MapListItem).ToList(),
            topCategories,
            suppliers.Take(4).Select(MapSupplierListItem).ToList());
    }

    // -------------------------------------------------------------- suppliers

    public async Task<SupplierDirectoryResponse> GetSupplierDirectoryAsync(
        string? q, bool? verifiedOnly, CancellationToken cancellationToken)
    {
        var items = await store.GetSupplierDirectoryAsync(q, verifiedOnly, cancellationToken);
        var listed = items.Select(MapSupplierListItem).ToList();
        return new SupplierDirectoryResponse(listed, listed.Count);
    }

    public async Task<SupplierDetailResponse> GetSupplierBySlugAsync(string slug, CancellationToken cancellationToken)
    {
        var supplier = await store.FindSupplierBySlugAsync(slug, cancellationToken)
            ?? throw new AppException(ErrorCodes.ResourceNotFound);
        if (supplier.Status != SupplierStatus.Active)
            throw new AppException(ErrorCodes.ResourceNotFound);

        var categoryNames = await store.GetSupplierCategoryNamesAsync(supplier.PublicId, cancellationToken);
        var products = await store.GetSupplierProductsAsync(slug, 8, cancellationToken);
        var directory = await store.GetSupplierDirectoryAsync(null, null, cancellationToken);
        var summary = directory.FirstOrDefault(d => d.PublicId == supplier.PublicId);

        return new SupplierDetailResponse(
            PublicId: supplier.PublicId.ToString(),
            Slug: supplier.Slug,
            Name: supplier.DisplayName,
            Verification: supplier.VerificationState.ToString(),
            Verified: supplier.VerificationState == VerificationState.Verified,
            Story: NonEmpty(supplier.Story),
            District: summary?.District,
            Location: NonEmpty(supplier.Profile?.OperationAddress ?? supplier.Profile?.CorporateAddress),
            ContactPerson: NonEmpty(supplier.Profile?.ContactPerson),
            Phone: NonEmpty(supplier.Profile?.Phone),
            Email: NonEmpty(supplier.Profile?.Email),
            Website: NonEmpty(supplier.Profile?.Website),
            ProductCount: products.Count == 0 ? null : products.Count,
            Categories: categoryNames,
            FeaturedProducts: products.Count == 0 ? null : products.Take(4).Select(MapListItem).ToList(),
            Logo: summary?.LogoStorageKey is { } key ? MediaRef(key, supplier.DisplayName) : null,
            Banner: null);
    }

    // -------------------------------------------------------------- references

    public async Task<IReadOnlyList<BrandRefWithCount>> GetBrandsAsync(string? categorySlug, CancellationToken cancellationToken)
    {
        var brands = await store.GetBrandsAsync(cancellationToken);
        var counts = new Dictionary<Guid, int>();
        foreach (var brand in brands)
        {
            counts[brand.PublicId] = await store.CountProductsAsync(
                new CatalogueProductQuery(categorySlug, null, null, brand.PublicId, null, false, null, 0, 1), cancellationToken);
        }
        return brands
            .Where(b => categorySlug is null || counts[b.PublicId] > 0 || b.Code == "generic")
            .Select(b => new BrandRefWithCount(new BrandRef(b.Code, b.Name), counts[b.PublicId]))
            .OrderByDescending(b => b.ProductCount)
            .ToList();
    }

    public async Task<IReadOnlyList<UnitOfMeasureRef>> GetUnitsAsync(CancellationToken cancellationToken)
        => (await store.GetUnitsAsync(cancellationToken))
            .Select(u => new UnitOfMeasureRef(u.Code, u.Name, u.PluralName, u.DimensionKind.ToString()))
            .ToList();

    public async Task<IReadOnlyList<AttributeRef>> GetAttributesAsync(string? categorySlug, CancellationToken cancellationToken)
    {
        var attributes = await store.GetAttributeDefinitionsAsync(cancellationToken);
        var units = await store.GetUnitsAsync(cancellationToken);
        var unitCodeByPublicId = units.ToDictionary(u => u.PublicId, u => u.Code);
        return attributes
            .OrderBy(a => a.DisplayName)
            .Select(a => new AttributeRef(
                a.Code,
                a.DisplayName,
                a.DataType.ToString(),
                a.UnitOfMeasureId is { } uid && unitCodeByPublicId.TryGetValue(uid, out var code) ? code : null,
                IsFilterable: false,
                null))
            .ToList();
    }

    // ------------------------------------------------------------------ maps

    private ProductListItem MapListItem(ProductSummaryProjection p)
    {
        var price = p.PriceAmount is { } amount && p.PriceState == PriceState.Public
            ? new PriceRef("Public", amount, p.CurrencyCode, p.MinOrderQuantity, null, null, null, null)
            : p.PriceState == PriceState.Public
                ? new PriceRef("OnRequest", null, p.CurrencyCode, p.MinOrderQuantity, null, null, null, null)
                : new PriceRef(p.PriceState.ToString(), null, p.CurrencyCode, p.MinOrderQuantity, null, null, null, null);

        return new ProductListItem(
            p.PublicId.ToString(), p.Slug, p.Name, p.Sku,
            new SupplierRef(
                p.SupplierId.ToString(), p.SupplierSlug, p.SupplierName, p.SupplierVerified,
                p.SupplierDistrict, p.SupplierVerified ? "Verified" : "Unverified"),
            new CategoryRef(p.CategorySlug, p.CategoryName),
            price,
            p.PriceState.ToString(),
            p.StockState.ToString(),
            p.MinOrderQuantity,
            p.LeadTimeDays,
            p.ItemType.ToString(),
            p.Attributes.Take(4).Select(MapAttributeValue).ToList(),
            p.PrimaryMediaStorageKey is { } key
                ? MediaRef(key, p.PrimaryMediaAlt is { Length: > 0 } alt ? alt : p.Name)
                : null);
    }

    private ProductAttributeValueRef MapAttributeValue(ProductAttributeProjection a)
    {
        var value = a.ValueText
            ?? a.ValueNumber?.ToString()
            ?? (a.ValueBool is { } b ? (b ? "Yes" : "No") : null);
        return new ProductAttributeValueRef(a.Code, a.Label, value, a.UnitCode);
    }

    private ProductDetailResponse MapDetail(ProductDetailProjection p, IReadOnlyList<ProductSummaryProjection> related)
    {
        // Migration-era suppliers ship Everyone/FullDetails (domain-model §3.4),
        // so every seeded price is public. The policy shape stays real for later
        // suppliers that hide prices: then PriceAmount must not reach the wire.
        var priceVisible = p.PriceState == PriceState.Public;
        var price = priceVisible && p.PriceAmount is { } amount
            ? new PriceRef(p.PriceState.ToString(), amount, p.CurrencyCode, p.MinOrderQuantity, null, null, null, p.PriceEffectiveAt)
            : null;

        var media = p.Media.Count == 0
            ? []
            : p.Media.OrderBy(m => m.SortOrder)
                .Select(m => MediaRef(m.StorageKey, m.AltText is { Length: > 0 } alt ? alt : p.Name))
                .ToList();

        var specs = p.Attributes.Count == 0
            ? null
            : new SpecificationsRef(
                [new SpecificationSection("Details", p.Attributes.Select(a => new SpecificationRow(
                    a.Code,
                    a.Label,
                    a.ValueText ?? a.ValueNumber?.ToString() ?? (a.ValueBool is { } b ? (b ? "Yes" : "No") : null),
                    a.DataType.ToString(),
                    a.UnitCode,
                    a.IsFilterable)).ToList())],
                CompletenessScore: null);

        var origin = p.CountryOfOriginCode is { } code
            ? new OriginRef(new CountryOfOriginRef(code, p.CountryOfOriginName ?? code), null)
            : null;

        return new ProductDetailResponse(
            p.PublicId.ToString(), p.Slug, p.Name, p.ItemType.ToString(), p.Sku, p.Status.ToString(),
            new SupplierDetailRef(
                p.SupplierId.ToString(), p.SupplierSlug, p.SupplierName,
                p.SupplierVerification.ToString(), null, p.MemberSince, null),
            price,
            PriceHistory: p.PriceHistory.Count == 0
                ? null
                : p.PriceHistory.Select(r => new PriceHistoryEntry(r.At, r.Amount)).ToList(),
            new StockRef(p.StockState.ToString(), p.StockTracked, p.LowStockThreshold),
            p.MinOrderQuantity, p.LeadTimeDays,
            p.BrandCode is { } bc ? new BrandRef(bc, p.BrandName ?? bc) : null,
            origin,
            NonEmpty(p.Summary), NonEmpty(p.Description),
            specs,
            media,
            new VisibilityRef(priceVisible, priceVisible ? null : "Prices are shared on request. Contact the supplier."),
            CanAddToRfq: false,
            related.Count == 0 ? null : related.Select(MapListItem).ToList(),
            RelatedSuppliers: null);
    }

    private SupplierDirectoryItem MapSupplierListItem(SupplierSummaryProjection s)
        => new(
            s.PublicId.ToString(), s.Slug, s.Name, s.Verification == VerificationState.Verified,
            s.District, s.Location, s.ProductCount,
            s.LogoStorageKey is { } key ? MediaRef(key, s.Name) : null);

    // ---------------------------------------------------------------- support

    /// <summary>A category node with its immediate children resolved.</summary>
    private static CategoryNode BuildNode(Category category, IReadOnlyList<Category> all)
    {
        var children = all
            .Where(c => c.ParentId == category.PublicId)
            .OrderBy(c => c.SortOrder)
            .ThenBy(c => c.Name)
            .Select(c => new CategoryNode(
                c.Slug, c.Name, c.NameSi,
                category.Slug, c.SortOrder, false,
                all.Where(g => g.ParentId == c.PublicId)
                    .OrderBy(g => g.SortOrder)
                    .Select(g => new CategoryNode(g.Slug, g.Name, g.NameSi, c.Slug, g.SortOrder, false, []))
                    .ToList()))
            .ToList();

        return new CategoryNode(
            category.Slug, category.Name, category.NameSi,
            all.FirstOrDefault(p => p.PublicId == category.ParentId)?.Slug,
            category.SortOrder, category.ParentId is null, children);
    }

    private async Task<IReadOnlyList<Facet>> BuildFacetsAsync(CatalogueProductQuery query, CancellationToken cancellationToken)
    {
        var facets = new List<Facet>();

        if (query.CategorySlug is { } categorySlug)
        {
            var category = await store.FindCategoryBySlugAsync(categorySlug, cancellationToken);
            if (category is not null)
            {
                var all = await store.GetAllCategoriesAsync(cancellationToken);
                var children = all.Where(c => c.ParentId == category.PublicId).Select(c => c.PublicId).ToList();
                if (children.Count > 0)
                {
                    var counts = await store.CountPublishedProductsByCategoryAsync(children, cancellationToken);
                    var options = all
                        .Where(c => children.Contains(c.PublicId) && counts.GetValueOrDefault(c.PublicId) > 0)
                        .OrderBy(c => c.SortOrder)
                        .Select(c => new FacetOption(c.Slug, c.Name, counts[c.PublicId]))
                        .ToList();
                    if (options.Count > 0)
                        facets.Add(new Facet("category", "Category", "hierarchy", options, null, null));
                }
            }
        }

        var origins = await store.GetOriginFacetCountsAsync(query, cancellationToken);
        if (origins.Count > 0)
            facets.Add(new Facet(
                "origin", "Country of origin", "enum",
                origins.Select(o => new FacetOption(o.Value, o.Label, o.Count)).ToList(), null, null));

        var verified = await store.GetVerifiedSupplierCountAsync(query, cancellationToken);
        facets.Add(new Facet("verified", "Verified suppliers", "boolean", null, null, verified));

        return facets;
    }

    private static ProductImageRef MediaRef(string storageKey, string alt)
        => new($"/api/v1/catalog/media/{storageKey} 1x", alt);

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static int ClampPageSize(int pageSize) => Math.Clamp(pageSize <= 0 ? DefaultPageSize : pageSize, 1, MaxPageSize);

    private static int clampedPage(int page, int pageSize) => Math.Max(page, 1);
}