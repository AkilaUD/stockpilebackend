namespace Stockpile.Contracts.Content;

// ============================================================================
// Public content contracts (api.md §2.12 "Content & SEO", domain-model §9).
//
// One shape serves the reference's editorial tree: news articles, completed
// project write-ups and guides are the same row with a different `kind`. Only
// published rows are ever served, and a field the platform does not hold is
// null rather than invented (api.md §3.5 truthful rendering) — `heroImageUrl`
// is null until the asset is ingested, and the frontend hides it.
// ============================================================================

/// <summary>Editorial bucket on an article (the reference's news categories).</summary>
public sealed record ContentCategoryRef(string Slug, string Name);

/// <summary>
/// Editorial tag on an article. The slug travels with the name because the
/// reference's news tree links each tag to <c>/news/tag/{slug}</c>.
/// </summary>
public sealed record ContentTagRef(string Slug, string Name);

/// <summary>
/// Listing row / card (api.md §2.12 <c>GET /content/articles</c>).
/// <para>
/// The body travels with the row: the reference's news list renders each post's
/// full text under its heading (verified 2026-10-14), not a teaser, and the
/// list is paged (six a page) so carrying it costs one request, not an N+1.
/// </para>
/// </summary>
public sealed record ArticleSummary(
    string PublicId,
    string Slug,
    string Kind,
    string Title,
    string? Excerpt,
    string Body,
    string? HeroImageUrl,
    IReadOnlyList<ContentCategoryRef> Categories,
    IReadOnlyList<ContentTagRef> Tags,
    DateTime? PublishedAt);

/// <summary>Full article (api.md §2.12 <c>GET /content/articles/{slug}</c> + the §3.1 SEO fields).</summary>
public sealed record ArticleDetail(
    string PublicId,
    string Slug,
    string Kind,
    string Title,
    string? Excerpt,
    string? HeroImageUrl,
    IReadOnlyList<ContentCategoryRef> Categories,
    IReadOnlyList<ContentTagRef> Tags,
    DateTime? PublishedAt,
    string Body,
    string? MetaTitle,
    string? MetaDescription,
    string? CanonicalUrl);

/// <summary>Offset pagination envelope with an exact total (api.md §2.12).</summary>
public sealed record PagedArticleResponse(
    IReadOnlyList<ArticleSummary> Items,
    int TotalItems,
    int Page,
    int PageSize);
