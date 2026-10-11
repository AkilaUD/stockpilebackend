using Microsoft.EntityFrameworkCore;
using Stockpile.Application.Catalogue;
using Stockpile.Domain.Entities.Catalogue;
using Stockpile.Domain.Entities.Reference;
using Stockpile.Domain.Enums;

namespace Stockpile.Infrastructure.Persistence;

/// <summary>
/// Live catalogue store over the schema's catalogue tables (domain-model §3/§4).
/// Read-only: the only writers are the seed pipeline and the Phase 2B–2E write
/// paths. Filtering is pushed into SQL; the service applies audience-policy
/// exposure and DTO shaping (api.md §2.3/§2.5) on top.
/// </summary>
public sealed class CatalogueStore(StockpileDbContext db) : ICatalogueStore
{
    // ------------------------------------------------------------ categories

    /// <inheritdoc />
    public async Task<IReadOnlyList<Category>> GetAllCategoriesAsync(CancellationToken cancellationToken)
        => await db.Categories.AsNoTracking().ToListAsync(cancellationToken);

    /// <inheritdoc />
    public Task<Category?> FindCategoryBySlugAsync(string slug, CancellationToken cancellationToken)
        => db.Categories.FirstOrDefaultAsync(c => c.Slug == slug, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<Category>> FindDescendantCategoriesAsync(
        string materializedPath, CancellationToken cancellationToken)
    {
        var prefix = materializedPath + "/";
        return await db.Categories.AsNoTracking()
            .Where(c => c.MaterializedPath == materializedPath || c.MaterializedPath.StartsWith(prefix))
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, int>> CountPublishedProductsByCategoryAsync(
        IReadOnlyCollection<Guid> categoryIds, CancellationToken cancellationToken)
    {
        var ids = categoryIds.ToList();
        if (ids.Count == 0) return new Dictionary<Guid, int>();
        return await db.Products.AsNoTracking()
            .Where(p => p.Status == ProductStatus.Published && ids.Contains(p.CategoryId))
            .GroupBy(p => p.CategoryId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);
    }

    // --------------------------------------------------------------- products

    /// <inheritdoc />
    public async Task<ProductDetailProjection?> FindPublishedProductBySlugAsync(
        string slug, CancellationToken cancellationToken)
    {
        var head = await DetailHeads(db.Products.AsNoTracking().Where(p => p.Slug == slug))
            .FirstOrDefaultAsync(cancellationToken);
        if (head is null) return null;
        return await ToDetailAsync(head, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ProductDetailProjection?> FindPublishedProductByIdAsync(
        Guid publicId, CancellationToken cancellationToken)
    {
        var head = await DetailHeads(db.Products.AsNoTracking().Where(p => p.PublicId == publicId))
            .FirstOrDefaultAsync(cancellationToken);
        if (head is null) return null;
        return await ToDetailAsync(head, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ProductSummaryProjection>> QueryProductsAsync(
        CatalogueProductQuery query, CancellationToken cancellationToken)
    {
        var products = FilteredProducts(query, published: true);
        var ids = await OrderProducts(products, query)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(p => p.PublicId)
            .ToListAsync(cancellationToken);
        return await ComposeAsync(ids, cancellationToken);
    }

    /// <inheritdoc />
    public Task<int> CountProductsAsync(CatalogueProductQuery query, CancellationToken cancellationToken)
        => FilteredProducts(query, published: true).CountAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<ProductSummaryProjection>> GetRelatedProductsAsync(
        Guid productId, int limit, CancellationToken cancellationToken)
    {
        var product = await db.Products.AsNoTracking()
            .FirstOrDefaultAsync(p => p.PublicId == productId, cancellationToken);
        if (product is null) return [];

        var ids = await db.Products.AsNoTracking()
            .Where(p => p.Status == ProductStatus.Published
                && p.PublicId != productId
                && (p.CategoryId == product.CategoryId || p.SupplierId == product.SupplierId))
            .OrderBy(p => p.CategoryId == product.CategoryId ? 0 : 1)
            .ThenBy(p => p.Name)
            .Take(limit)
            .Select(p => p.PublicId)
            .ToListAsync(cancellationToken);
        return await ComposeAsync(ids, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ProductSummaryProjection>> GetSupplierProductsAsync(
        string supplierSlug, int limit, CancellationToken cancellationToken)
    {
        var ids = await db.Products.AsNoTracking()
            .Where(p => p.Status == ProductStatus.Published && p.SupplierId == db.Suppliers
                .Where(s => s.Slug == supplierSlug).Select(s => s.PublicId).FirstOrDefault())
            .OrderByDescending(p => p.PublishedAt)
            .Take(limit)
            .Select(p => p.PublicId)
            .ToListAsync(cancellationToken);
        return await ComposeAsync(ids, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetSearchSuggestionsAsync(
        string query, int limit, CancellationToken cancellationToken)
    {
        var pattern = $"%{query}%";
        var products = await db.Products.AsNoTracking()
            .Where(p => p.Status == ProductStatus.Published && EF.Functions.Like(p.Name, pattern))
            .OrderBy(p => p.Name)
            .Take(limit)
            .Select(p => p.Name)
            .ToListAsync(cancellationToken);
        var categories = await db.Categories.AsNoTracking()
            .Where(c => c.IsActive && EF.Functions.Like(c.Name, pattern))
            .OrderBy(c => c.Name)
            .Take(limit)
            .Select(c => c.Name)
            .ToListAsync(cancellationToken);
        return products.Concat(categories).Distinct().Take(limit).ToList();
    }

    // ----------------------------------------------------------------- facets

    /// <inheritdoc />
    public async Task<IReadOnlyList<FacetCount>> GetOriginFacetCountsAsync(
        CatalogueProductQuery query, CancellationToken cancellationToken)
    {
        var products = FilteredProducts(query, published: true);
        return await products
            .Where(p => p.CountryOfOriginCode != null)
            .GroupBy(p => p.CountryOfOriginCode!)
            .Select(g => new { Code = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .Take(10)
            .Select(x => new FacetCount(x.Code, x.Code, x.Count))
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<int> GetVerifiedSupplierCountAsync(
        CatalogueProductQuery query, CancellationToken cancellationToken)
    {
        var verifiedIds = db.Suppliers.AsNoTracking()
            .Where(s => s.VerificationState == VerificationState.Verified)
            .Select(s => s.PublicId)
            .ToList();
        return await FilteredProducts(query, published: true)
            .CountAsync(p => verifiedIds.Contains(p.SupplierId), cancellationToken);
    }

    // -------------------------------------------------------------- suppliers

    /// <inheritdoc />
    public async Task<IReadOnlyList<SupplierSummaryProjection>> GetSupplierDirectoryAsync(
        string? q, bool? verifiedOnly, CancellationToken cancellationToken)
    {
        var query = db.Suppliers.AsNoTracking().Where(s => s.Status == SupplierStatus.Active);
        if (verifiedOnly == true) query = query.Where(s => s.VerificationState == VerificationState.Verified);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var pattern = $"%{q.Trim()}%";
            query = query.Where(s => EF.Functions.Like(s.DisplayName, pattern) || EF.Functions.Like(s.Slug, pattern));
        }

        var rows = await query
            .OrderBy(s => s.DisplayName)
            .Select(s => new
            {
                s.PublicId,
                s.Slug,
                s.DisplayName,
                s.VerificationState,
                s.Profile!.OperationAddress,
                s.LogoMediaId,
                ProductCount = db.Products.Count(p => p.SupplierId == s.PublicId && p.Status == ProductStatus.Published),
            })
            .ToListAsync(cancellationToken);

        var logoKeys = rows
            .Where(r => r.LogoMediaId is not null)
            .Select(r => r.LogoMediaId!.Value)
            .ToHashSet();

        var mediaKeys = await db.MediaAssets.AsNoTracking()
            .Where(m => logoKeys.Contains(m.PublicId))
            .ToDictionaryAsync(m => m.PublicId, m => m.StorageKey, cancellationToken);

        return rows.Select(r => new SupplierSummaryProjection(
            r.PublicId, r.Slug, r.DisplayName, r.VerificationState,
            District: null, Location: r.OperationAddress,
            r.ProductCount,
            r.LogoMediaId is { } id && mediaKeys.TryGetValue(id, out var key) ? key : null))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<Supplier?> FindSupplierBySlugAsync(string slug, CancellationToken cancellationToken)
        => await db.Suppliers
            .Include(s => s.Profile)
            .FirstOrDefaultAsync(s => s.Slug == slug, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetSupplierCategoryNamesAsync(
        Guid supplierId, CancellationToken cancellationToken)
        => await (
            from sc in db.SupplierCategories
            join c in db.Categories on sc.CategoryId equals c.PublicId
            where sc.SupplierId == supplierId
            orderby sc.SpecialtyLevel == SpecialtyLevel.Primary ? 0 : 1, c.Name
            select c.Name)
            .ToListAsync(cancellationToken);

    // -------------------------------------------------------------- references

    /// <inheritdoc />
    public async Task<IReadOnlyList<Brand>> GetBrandsAsync(CancellationToken cancellationToken)
        => await db.Brands.AsNoTracking().OrderBy(b => b.Name).ToListAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<UnitOfMeasure>> GetUnitsAsync(CancellationToken cancellationToken)
        => await db.UnitOfMeasures.AsNoTracking().OrderBy(u => u.Name).ToListAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<AttributeDefinition>> GetAttributeDefinitionsAsync(CancellationToken cancellationToken)
        => await db.AttributeDefinitions.AsNoTracking().ToListAsync(cancellationToken);

    /// <inheritdoc />
    public Task<Country?> FindCountryAsync(string code, CancellationToken cancellationToken)
        => db.Countries.FirstOrDefaultAsync(c => c.Code == code, cancellationToken);

    // ---------------------------------------------------------------- plumbing

    /// <summary>Products restricted by the active filters.</summary>
    /// <remarks>
    /// Every predicate runs against entity columns BEFORE any projection is
    /// composed: EF Core refuses member access over a record constructed in
    /// the query (the failure mode that took every product surface down), so
    /// supplier/category text matches arrive here as materialised id lists
    /// instead of join-column predicates, and stock/verification filters
    /// correlate through key lookups rather than joined row members.
    /// </remarks>
    private IQueryable<Product> FilteredProducts(CatalogueProductQuery query, bool published)
    {
        var products = db.Products.AsNoTracking();
        if (published) products = products.Where(p => p.Status == ProductStatus.Published);

        if (!string.IsNullOrWhiteSpace(query.CategorySlug))
        {
            var slug = query.CategorySlug.Trim();
            var category = db.Categories.AsNoTracking().FirstOrDefault(c => c.Slug == slug);
            if (category is not null)
            {
                var prefix = category.MaterializedPath + "/";
                var ids = db.Categories.AsNoTracking()
                    .Where(c => c.MaterializedPath == category.MaterializedPath || c.MaterializedPath.StartsWith(prefix))
                    .Select(c => c.PublicId)
                    .ToList();
                products = products.Where(p => ids.Contains(p.CategoryId));
            }
        }

        if (!string.IsNullOrWhiteSpace(query.Q))
        {
            var pattern = $"%{query.Q.Trim()}%";
            var supplierIds = SupplierIdsMatchingDisplayName(pattern);
            var categoryIds = db.Categories.AsNoTracking()
                .Where(c => EF.Functions.Like(c.Name, pattern))
                .Select(c => c.PublicId)
                .ToList();
            products = products.Where(p =>
                EF.Functions.Like(p.Name, pattern)
                || EF.Functions.Like(p.Sku, pattern)
                || (p.Description != null && EF.Functions.Like(p.Description, pattern))
                || supplierIds.Contains(p.SupplierId)
                || categoryIds.Contains(p.CategoryId));
        }

        if (query.BrandId is { } brandId) products = products.Where(p => p.BrandId == brandId);
        if (!string.IsNullOrWhiteSpace(query.CountryCode)) products = products.Where(p => p.CountryOfOriginCode == query.CountryCode);
        if (query.VerifiedOnly)
        {
            var verifiedIds = db.Suppliers.AsNoTracking()
                .Where(s => s.VerificationState == VerificationState.Verified)
                .Select(s => s.PublicId)
                .ToList();
            products = products.Where(p => verifiedIds.Contains(p.SupplierId));
        }

        if (query.InStockOnly == true)
        {
            var inStockIds = db.ProductStocks.AsNoTracking()
                .Where(st => st.StockState == StockState.InStock || st.StockState == StockState.LowStock)
                .Select(st => st.ProductId)
                .ToList();
            products = products.Where(p => inStockIds.Contains(p.PublicId));
        }

        return products;
    }

    /// <summary>Suppliers whose display name matches the LIKE pattern (server-side, then materialised).</summary>
    private List<Guid> SupplierIdsMatchingDisplayName(string pattern) =>
        db.Suppliers.AsNoTracking()
            .Where(s => EF.Functions.Like(s.DisplayName, pattern))
            .Select(s => s.PublicId)
            .ToList();

    private IQueryable<Product> OrderProducts(IQueryable<Product> products, CatalogueProductQuery q)
    {
        var text = q.Q?.Trim();
        return q.Sort switch
        {
            "price_asc" => products.OrderBy(p => db.ProductPrices
                .Where(x => x.ProductId == p.PublicId)
                .Select(x => (decimal?)x.Amount)
                .FirstOrDefault()),
            "price_desc" => products.OrderByDescending(p => db.ProductPrices
                .Where(x => x.ProductId == p.PublicId)
                .Select(x => (decimal?)x.Amount)
                .FirstOrDefault()),
            "name" => products.OrderBy(p => p.Name),
            "relevance" when string.IsNullOrWhiteSpace(text) => products.OrderByDescending(p => p.PublishedAt),
            "relevance" => OrderByRelevance(products, text!),
            _ => products.OrderByDescending(p => p.PublishedAt),
        };
    }

    /// <summary>Text relevance: the same scoring the joined-row query used, kept translatable.</summary>
    private IQueryable<Product> OrderByRelevance(IQueryable<Product> products, string text)
    {
        var pattern = $"%{text}%";
        var supplierIds = SupplierIdsMatchingDisplayName(pattern);
        return products.OrderByDescending(p =>
            (p.Name.StartsWith(text) ? 10 : 0)
            + (p.Name.Contains(text) ? 8 : 0)
            + (p.Sku.Contains(text) ? 6 : 0)
            + (supplierIds.Contains(p.SupplierId) ? 4 : 0));
    }

    /// <summary>Detail head for one product with all joined headline rows.</summary>
    /// <remarks>
    /// The id/slug predicate must be part of <paramref name="products"/> —
    /// filtering the constructed head afterwards composes member access over
    /// the projection, which EF Core cannot translate.
    /// </remarks>
    private IQueryable<ProductDetailHead> DetailHeads(IQueryable<Product> products) =>
        from p in products
        join s in db.Suppliers on p.SupplierId equals s.PublicId
        join c in db.Categories on p.CategoryId equals c.PublicId
        join pr in db.ProductPrices on p.PublicId equals pr.ProductId into prj
        from price in prj.DefaultIfEmpty()
        join st in db.ProductStocks on p.PublicId equals st.ProductId into stj
        from stock in stj.DefaultIfEmpty()
        join co in db.Countries on p.CountryOfOriginCode equals co.Code into coj
        from country in coj.DefaultIfEmpty()
        where p.Status == ProductStatus.Published
        select new ProductDetailHead(
            p.PublicId, p.Slug, p.Name, p.Sku, p.ItemType, p.Status,
            s.PublicId, s.Slug, s.DisplayName, s.VerificationState,
            c.PublicId, c.Slug, c.Name,
            p.Summary, p.Description, p.MinOrderQuantity, p.LeadTimeDays,
            p.BrandId != null ? db.Brands.Where(b => b.PublicId == p.BrandId).Select(b => b.Code).FirstOrDefault() : null,
            p.BrandId != null ? db.Brands.Where(b => b.PublicId == p.BrandId).Select(b => b.Name).FirstOrDefault() : null,
            p.CountryOfOriginCode,
            country != null ? country.Name : null,
            price != null ? price.PriceState : PriceState.Public,
            price != null ? price.Amount : null,
            price != null ? price.CurrencyCode : "LKR",
            price != null ? price.EffectiveAt : null,
            stock != null ? stock.StockState : StockState.EnquireForAvailability,
            stock != null ? stock.IsStockTracked : false,
            stock != null ? stock.LowStockThreshold : null);

    private async Task<ProductDetailProjection> ToDetailAsync(ProductDetailHead h, CancellationToken cancellationToken)
    {
        var attributes = await AttributesForAsync(h.PublicId, cancellationToken);
        var media = await MediaForAsync(h.PublicId, cancellationToken);
        var history = await db.ProductPriceHistory.AsNoTracking()
            .Where(r => r.ProductId == h.PublicId)
            .OrderBy(r => r.ChangedAt)
            .Select(r => new PriceHistoryProjection(r.ChangedAt, r.NewAmount))
            .ToListAsync(cancellationToken);
        return new ProductDetailProjection(
            h.PublicId, h.Slug, h.Name, h.Sku, h.ItemType, h.Status,
            h.SupplierId, h.SupplierSlug, h.SupplierName, h.SupplierVerification,
            SupplierAudience.Everyone, SupplierVisibility.FullDetails,
            h.CategoryId, h.CategorySlug, h.CategoryName,
            h.Summary, h.Description, h.MinOrderQuantity, h.LeadTimeDays,
            h.BrandCode, h.BrandName, h.CountryCode, h.CountryName,
            h.PriceState, h.PriceAmount, h.CurrencyCode, h.PriceEffectiveAt,
            h.StockState, h.StockTracked, h.LowStockThreshold,
            MemberSince: null,
            PriceHistory: history,
            attributes, media);
    }

    private async Task<IReadOnlyList<ProductSummaryProjection>> ComposeAsync(
        List<Guid> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0) return [];

        var heads = await HeadQuery(db.Products.AsNoTracking().Where(p => ids.Contains(p.PublicId)))
            .ToListAsync(cancellationToken);

        var attributes = await AttributesForManyAsync(ids, cancellationToken);
        var media = await MediaForManyAsync(ids, cancellationToken);

        var byId = heads.ToDictionary(h => h.ProductId);
        return ids
            .Where(byId.ContainsKey)
            .Select(id =>
            {
                var h = byId[id];
                var attrs = attributes.TryGetValue(id, out var a) ? a : [];
                var primary = media.TryGetValue(id, out var m) && m.Count > 0 ? m[0] : null;
                return new ProductSummaryProjection(
                    h.ProductId, h.Slug, h.Name, h.Sku,
                    h.SupplierId, h.SupplierSlug, h.SupplierName,
                    h.SupplierVerification == VerificationState.Verified,
                    SupplierDistrict: null,
                    h.CategorySlug, h.CategoryName,
                    h.PriceState, h.PriceAmount, h.CurrencyCode,
                    h.MinOrderQuantity, h.LeadTimeDays, h.ItemType, h.StockState,
                    primary?.StorageKey, primary?.AltText,
                    attrs);
            })
            .ToList();
    }

    /// <summary>Summary head for the given product set; the id filter must be part of the source.</summary>
    private IQueryable<ProductSummaryHead> HeadQuery(IQueryable<Product> products) =>
        from p in products
        join s in db.Suppliers on p.SupplierId equals s.PublicId
        join c in db.Categories on p.CategoryId equals c.PublicId
        join pr in db.ProductPrices on p.PublicId equals pr.ProductId into prj
        from price in prj.DefaultIfEmpty()
        join st in db.ProductStocks on p.PublicId equals st.ProductId into stj
        from stock in stj.DefaultIfEmpty()
        select new ProductSummaryHead(
            p.PublicId, p.Slug, p.Name, p.Sku,
            s.PublicId, s.Slug, s.DisplayName, s.VerificationState,
            c.Slug, c.Name,
            price != null ? price.PriceState : PriceState.Public,
            price != null ? price.Amount : null,
            price != null ? price.CurrencyCode : "LKR",
            p.MinOrderQuantity, p.LeadTimeDays, p.ItemType,
            stock != null ? stock.StockState : StockState.EnquireForAvailability);

    private async Task<IReadOnlyDictionary<Guid, IReadOnlyList<ProductAttributeProjection>>> AttributesForManyAsync(
        List<Guid> productIds, CancellationToken cancellationToken)
    {
        var items = await (
            from v in db.ProductAttributeValues
            join d in db.AttributeDefinitions on v.AttributeId equals d.PublicId
            join uom in db.UnitOfMeasures on d.UnitOfMeasureId equals uom.PublicId into uomj
            from uom in uomj.DefaultIfEmpty()
            where productIds.Contains(v.ProductId)
            select new
            {
                v.ProductId,
                Value = new ProductAttributeProjection(
                    d.Code, d.DisplayName,
                    v.ValueText, v.ValueNumber, v.ValueBool,
                    d.DataType,
                    uom != null ? uom.Code : null,
                    IsFilterable: false),
            })
            .ToListAsync(cancellationToken);

        return items
            .GroupBy(x => x.ProductId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ProductAttributeProjection>)g.Select(x => x.Value).ToList());
    }

    private async Task<IReadOnlyList<ProductAttributeProjection>> AttributesForAsync(
        Guid productId, CancellationToken cancellationToken)
    {
        var grouped = await AttributesForManyAsync([productId], cancellationToken);
        return grouped.TryGetValue(productId, out var value) ? value : [];
    }

    private async Task<IReadOnlyDictionary<Guid, IReadOnlyList<ProductMediaProjection>>> MediaForManyAsync(
        List<Guid> productIds, CancellationToken cancellationToken)
    {
        var items = await (
            from pm in db.ProductMedias
            join ma in db.MediaAssets on pm.MediaAssetId equals ma.PublicId
            where productIds.Contains(pm.ProductId)
            orderby pm.SortOrder
            select new
            {
                pm.ProductId,
                Media = new ProductMediaProjection(ma.StorageKey, pm.AltText, pm.SortOrder, pm.IsPrimary),
            })
            .ToListAsync(cancellationToken);

        return items
            .GroupBy(x => x.ProductId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<ProductMediaProjection>)g
                    .OrderByDescending(m => m.Media.IsPrimary)
                    .ThenBy(m => m.Media.SortOrder)
                    .Select(m => m.Media)
                    .ToList());
    }

    private async Task<IReadOnlyList<ProductMediaProjection>> MediaForAsync(
        Guid productId, CancellationToken cancellationToken)
    {
        var grouped = await MediaForManyAsync([productId], cancellationToken);
        return grouped.TryGetValue(productId, out var value) ? value : [];
    }

    // ------------------------------------------------------------- record rows

    private sealed record ProductSummaryHead(
        Guid ProductId, string Slug, string Name, string Sku,
        Guid SupplierId, string SupplierSlug, string SupplierName, VerificationState SupplierVerification,
        string CategorySlug, string CategoryName,
        PriceState PriceState, decimal? PriceAmount, string CurrencyCode,
        decimal? MinOrderQuantity, int? LeadTimeDays, ItemType ItemType, StockState StockState);

    private sealed record ProductDetailHead(
        Guid PublicId, string Slug, string Name, string Sku, ItemType ItemType, ProductStatus Status,
        Guid SupplierId, string SupplierSlug, string SupplierName, VerificationState SupplierVerification,
        Guid CategoryId, string CategorySlug, string CategoryName,
        string? Summary, string? Description, decimal? MinOrderQuantity, int? LeadTimeDays,
        string? BrandCode, string? BrandName, string? CountryCode, string? CountryName,
        PriceState PriceState, decimal? PriceAmount, string CurrencyCode, DateTime? PriceEffectiveAt,
        StockState StockState, bool StockTracked, int? LowStockThreshold);
}