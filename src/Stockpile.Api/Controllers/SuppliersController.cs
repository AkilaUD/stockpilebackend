using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Stockpile.Api.RateLimiting;
using Stockpile.Application.Catalogue;
using Stockpile.Contracts.Catalogue;

namespace Stockpile.Api.Controllers;

/// <summary>
/// Public supplier surfaces (api.md §2.4). Directory reads are anonymous under
/// the deny-by-default fallback; responses carry public data only — internal
/// contacts, documents and subscription state never leave the write side
/// (§25). Districts are not yet populated for migrated suppliers, so the map
/// view stays honest: no fabricated geography (api.md §3.5).
/// </summary>
[ApiController]
[ApiVersion(1.0)]
[Route("api/v{version:apiVersion}/suppliers")]
[AllowAnonymous]
[EnableRateLimiting(RateLimitPolicies.PublicApi)]
public sealed class SuppliersController(CatalogueService catalogue) : ControllerBase
{
    /// <summary>Directory (api.md §2.4): first-party listed suppliers, live product counts.</summary>
    [HttpGet]
    public Task<SupplierDirectoryResponse> Directory(
        [FromQuery] string? q = null, [FromQuery] bool verifiedOnly = false,
        CancellationToken cancellationToken = default)
        => catalogue.GetSupplierDirectoryAsync(q, verifiedOnly, cancellationToken);

    /// <summary>Microsite payload (api.md §2.4): identity, story, contact, featured products.</summary>
    [HttpGet("{slug}")]
    public Task<SupplierDetailResponse> Supplier(string slug, CancellationToken cancellationToken)
        => catalogue.GetSupplierBySlugAsync(slug, cancellationToken);

    /// <summary>This supplier's published products only.</summary>
    [HttpGet("{slug}/products")]
    public Task<PagedProductResponse> Products(
        string slug, [FromQuery] int page = 1, [FromQuery] int pageSize = 48,
        CancellationToken cancellationToken = default)
        => catalogue.GetSupplierProductsAsync(slug, page, pageSize, cancellationToken);
}