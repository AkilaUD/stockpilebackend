using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Stockpile.Api.RateLimiting;
using Stockpile.Application.Catalogue;
using Stockpile.Contracts.Catalogue;

namespace Stockpile.Api.Controllers;

/// <summary>
/// Task 2E — the public catalogue (api.md §2.3). Every read is anonymous under
/// the deny-by-default fallback (<c>[AllowAnonymous]</c> is the metadata the
/// policy needs, T6.1), and the burst budgets follow api.md §1.8. Read-only
/// projections — prices and stock are exposed by the service under the
/// supplier audience policy (domain-model §3.4), never invented to fill a gap.
/// </summary>
[ApiController]
[ApiVersion(1.0)]
[Route("api/v{version:apiVersion}/catalog")]
[AllowAnonymous]
[EnableRateLimiting(RateLimitPolicies.PublicApi)]
public sealed class CatalogueController(CatalogueService catalogue) : ControllerBase
{
    /// <summary>Masthead pulse counts (§8.1/§8.3): live totals, exact per the current catalogue.</summary>
    [HttpGet("summary")]
    public Task<CatalogueSummary> Summary(CancellationToken cancellationToken)
        => catalogue.GetSummaryAsync(cancellationToken);

    /// <summary>Full category tree (api.md §2.3) — department mega-menu source.</summary>
    [HttpGet("categories")]
    public Task<IReadOnlyList<CategoryNode>> Categories(CancellationToken cancellationToken)
        => catalogue.GetCategoryTreeAsync(cancellationToken);

    /// <summary>Category node with its immediate children and the paged product listing.</summary>
    /// <remarks>
    /// Category slugs are full materialised paths (api.md §2.3), so the route
    /// is a catch-all: it accepts both the literal multi-segment form
    /// (<c>/categories/building-materials/cement-concrete</c>) and the
    /// single-segment percent-encoded form clients produce with
    /// <c>encodeURIComponent</c> (<c>%2F</c> survives route matching encoded
    /// inside one segment). The lookup always receives the decoded path —
    /// category slugs never contain '%', so the unescape is a no-op for
    /// already-decoded input.
    /// </remarks>
    [HttpGet("categories/{*slug}")]
    public Task<CategoryDetailResponse> Category(
        string slug, [FromQuery] int page = 1, [FromQuery] int pageSize = 48,
        CancellationToken cancellationToken = default)
        => catalogue.GetCategoryDetailAsync(Uri.UnescapeDataString(slug), page, pageSize, cancellationToken);

    /// <summary>Paged product listing with shareable filters (api.md §2.3/§1.3).</summary>
    [HttpGet("products")]
    public Task<PagedProductResponse> Products(
        [FromQuery] string? category = null, [FromQuery] string? q = null, [FromQuery] string? sort = null,
        [FromQuery] Guid? brandId = null, [FromQuery] string? countryCode = null, [FromQuery] bool verifiedOnly = false,
        [FromQuery] bool? inStockOnly = null, [FromQuery] int page = 1, [FromQuery] int pageSize = 48,
        CancellationToken cancellationToken = default)
        => catalogue.GetProductsAsync(
            category, q, sort, brandId, countryCode, verifiedOnly, inStockOnly, page, pageSize, cancellationToken);

    /// <summary>Product detail (api.md §3.4). 404 RESOURCE_NOT_FOUND for unpublished/unknown slugs.</summary>
    [HttpGet("products/{slug}")]
    [EnableRateLimiting(RateLimitPolicies.PublicApi)]
    public Task<ProductDetailResponse> Product(string slug, CancellationToken cancellationToken)
        => catalogue.GetProductBySlugAsync(slug, cancellationToken);

    /// <summary>Related products block (same category or supplier, excluding self).</summary>
    [HttpGet("products/{productId:guid}/related")]
    public async Task<IReadOnlyList<ProductListItem>> Related(
        Guid productId, CancellationToken cancellationToken)
        => await catalogue.GetRelatedProductsAsync(productId, cancellationToken);

    /// <summary>Reference data: filterable attribute definitions (api.md §2.3).</summary>
    [HttpGet("attributes")]
    public Task<IReadOnlyList<AttributeRef>> Attributes(CancellationToken cancellationToken)
        => catalogue.GetAttributesAsync(null, cancellationToken);

    /// <summary>Brands with live product counts, optionally scoped to a category.</summary>
    [HttpGet("brands")]
    public Task<IReadOnlyList<BrandRefWithCount>> Brands(
        [FromQuery] string? category, CancellationToken cancellationToken)
        => catalogue.GetBrandsAsync(category, cancellationToken);

    /// <summary>Unit-of-measure reference rows.</summary>
    [HttpGet("units")]
    public Task<IReadOnlyList<UnitOfMeasureRef>> Units(CancellationToken cancellationToken)
        => catalogue.GetUnitsAsync(cancellationToken);
}