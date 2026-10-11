using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Stockpile.Api.RateLimiting;
using Stockpile.Application.Catalogue;
using Stockpile.Contracts.Catalogue;

namespace Stockpile.Api.Controllers;

/// <summary>
/// Public search surfaces (api.md §2.3): the main search belongs to
/// <c>/catalog/search</c>; the lightweight typeahead and command-search
/// overlay live at <c>/search/suggestions</c> and <c>/search/command</c>.
/// All three share the <c>search.public</c> budget (60/min per client, §1.8):
/// a scraper's probing burst trips the security burst monitor.
/// </summary>
[ApiController]
[ApiVersion(1.0)]
[AllowAnonymous]
[EnableRateLimiting(RateLimitPolicies.SearchPublic)]
public sealed class PublicSearchController(CatalogueService catalogue) : ControllerBase
{
    [HttpGet("api/v{version:apiVersion}/catalog/search")]
    public Task<SearchResponse> Search(
        [FromQuery] string q, [FromQuery] string? category = null, [FromQuery] string? sort = null,
        [FromQuery] Guid? brandId = null, [FromQuery] string? countryCode = null, [FromQuery] bool verifiedOnly = false,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 24,
        CancellationToken cancellationToken = default)
        => catalogue.SearchAsync(q, category, sort, brandId, countryCode, verifiedOnly, page, pageSize, cancellationToken);

    [HttpGet("api/v{version:apiVersion}/search/suggestions")]
    public Task<IReadOnlyList<string>> Suggestions(
        [FromQuery] string q, CancellationToken cancellationToken)
        => catalogue.GetSuggestionsAsync(q, cancellationToken);

    [HttpGet("api/v{version:apiVersion}/search/command")]
    public Task<CommandSearchResponse> Command(
        [FromQuery] string q, CancellationToken cancellationToken)
        => catalogue.CommandSearchAsync(q, cancellationToken);
}