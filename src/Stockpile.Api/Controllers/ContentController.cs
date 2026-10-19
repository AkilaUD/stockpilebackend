using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Stockpile.Api.RateLimiting;
using Stockpile.Application.Content;
using Stockpile.Contracts.Content;
using Stockpile.Domain.Enums;

namespace Stockpile.Api.Controllers;

/// <summary>
/// Task 3E — the public content tree (api.md §2.12). Anonymous reads under the
/// deny-by-default fallback (<c>[AllowAnonymous]</c> is the metadata the policy
/// needs, T6.1) with the api.md §1.8 burst budget.
/// <para>
/// <c>/projects</c> and <c>/guides</c> are the API contract's own paths and are
/// views of the one content aggregate filtered by kind — not separate tables
/// with separate data. Nothing here writes: editorial authoring is the admin
/// surface (api.md §2.13), which has not landed.
/// </para>
/// </summary>
[ApiController]
[ApiVersion(1.0)]
[Route("api/v{version:apiVersion}/content")]
[AllowAnonymous]
[EnableRateLimiting(RateLimitPolicies.PublicApi)]
public sealed class ContentController(ContentService content) : ControllerBase
{
    /// <summary>Published articles, newest first (api.md §2.12).</summary>
    [HttpGet("articles")]
    public Task<PagedArticleResponse> Articles(
        [FromQuery] string? kind = null,
        [FromQuery] string? tag = null,
        [FromQuery] string? category = null,
        [FromQuery] string? locale = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 0,
        CancellationToken cancellationToken = default)
        => content.GetArticlesAsync(
            ResolveKinds(kind), category, tag, locale, page, pageSize, cancellationToken);

    /// <summary>One published article by slug (api.md §2.12). Unknown slug = 404 RESOURCE_NOT_FOUND.</summary>
    [HttpGet("articles/{slug}")]
    public Task<ArticleDetail> Article(
        string slug,
        [FromQuery] string? locale = null,
        CancellationToken cancellationToken = default)
        => content.GetArticleAsync(slug, locale, cancellationToken);

    /// <summary>Completed project write-ups (api.md §2.12 <c>GET /content/projects</c>).</summary>
    [HttpGet("projects")]
    public Task<PagedArticleResponse> Projects(
        [FromQuery] string? tag = null,
        [FromQuery] string? category = null,
        [FromQuery] string? locale = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 0,
        CancellationToken cancellationToken = default)
        => content.GetArticlesAsync(
            [ContentKind.ProjectCaseStudy], category, tag, locale, page, pageSize, cancellationToken);

    /// <summary>
    /// Guide articles (api.md §2.12 <c>GET /content/guides</c>): the two guide
    /// kinds the domain declares. Reference guides that are not editorial
    /// articles — the how-to pages — stay as the transcribed static pages they
    /// already are; this endpoint does not duplicate them.
    /// </summary>
    [HttpGet("guides")]
    public Task<PagedArticleResponse> Guides(
        [FromQuery] string? tag = null,
        [FromQuery] string? category = null,
        [FromQuery] string? locale = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 0,
        CancellationToken cancellationToken = default)
        => content.GetArticlesAsync(
            [ContentKind.CategoryGuide, ContentKind.MaterialGuide],
            category, tag, locale, page, pageSize, cancellationToken);

    /// <summary>The editorial buckets the news tree navigates by.</summary>
    [HttpGet("categories")]
    public Task<IReadOnlyList<ContentCategoryRef>> Categories(CancellationToken cancellationToken)
        => content.GetCategoriesAsync(cancellationToken);

    /// <summary>
    /// Resolves the <c>kind=</c> filter. Null (absent) means every kind; a
    /// parsed name filters to it; an unrecognised value yields an empty list,
    /// which the store answers with an empty page rather than the whole corpus.
    /// </summary>
    private static IReadOnlyList<ContentKind>? ResolveKinds(string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind))
        {
            return null;
        }

        return Enum.TryParse(kind, ignoreCase: true, out ContentKind parsed) ? [parsed] : [];
    }
}
