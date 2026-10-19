using Stockpile.Application.Common.Errors;
using Stockpile.Contracts.Content;
using Stockpile.Contracts.Errors;
using Stockpile.Domain.Enums;

namespace Stockpile.Application.Content;

/// <summary>
/// The public content use cases (api.md §2.12, domain-model §9). Reads only:
/// the reference's editorial tree is served, never authored, from this surface.
/// <para>
/// The list is filterable by <c>kind</c>, <c>tag</c> and the surface's own
/// bucket (<c>category</c>), and is always paged with an exact total. An
/// unknown tag or bucket is an empty page, not an error — a filter that matches
/// nothing is a normal result. An unknown <b>slug</b> is a 404.
/// </para>
/// </summary>
public sealed class ContentService(IContentStore store)
{
    /// <summary>Default page size for the editorial lists.</summary>
    public const int DefaultPageSize = 12;

    /// <summary>Hard ceiling on what the public surface will return in one page.</summary>
    public const int MaxPageSize = 48;

    /// <summary>Highest page number accepted — keeps the offset arithmetic inside Int32.</summary>
    private const int MaxPage = 10_000;

    private static readonly HashSet<string> SupportedLocales = new(StringComparer.Ordinal) { "en", "si" };

    /// <summary>One page of published content (api.md §2.12 <c>GET /content/articles</c>).</summary>
    public Task<PagedArticleResponse> GetArticlesAsync(
        IReadOnlyList<ContentKind>? kinds,
        string? categorySlug,
        string? tagSlug,
        string? locale,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
        => ListAsync(
            new ContentQuery(
                kinds,
                Trim(categorySlug),
                Trim(tagSlug),
                NormaliseLocale(locale),
                Math.Clamp(page, 1, MaxPage),
                ClampPageSize(pageSize)),
            cancellationToken);

    /// <summary>One published article (api.md §2.12 <c>GET /content/articles/{slug}</c>).</summary>
    public async Task<ArticleDetail> GetArticleAsync(
        string slug, string? locale, CancellationToken cancellationToken)
    {
        ContentArticle? article = await store.GetArticleAsync(
            slug.Trim(), NormaliseLocale(locale), cancellationToken);
        if (article is null)
        {
            throw new AppException(ErrorCodes.ResourceNotFound);
        }

        return new ArticleDetail(
            article.Item.PublicId.ToString(),
            article.Item.Slug,
            article.Item.Kind.ToString(),
            article.Item.Title,
            article.Item.Excerpt,
            MediaUrl(article.HeroStorageKey),
            [.. article.Categories.Select(bucket => new ContentCategoryRef(bucket.Slug, bucket.Name))],
            [.. article.Tags.Select(tag => new ContentTagRef(tag.Slug, tag.Name))],
            article.Item.PublishedAt,
            article.Item.Body,
            article.Item.MetaTitle,
            article.Item.MetaDescription,
            article.Item.CanonicalUrl);
    }

    /// <summary>The editorial buckets the news tree navigates by.</summary>
    public async Task<IReadOnlyList<ContentCategoryRef>> GetCategoriesAsync(CancellationToken cancellationToken)
        => [.. (await store.GetCategoriesAsync(cancellationToken))
            .Select(bucket => new ContentCategoryRef(bucket.Slug, bucket.Name))];

    private async Task<PagedArticleResponse> ListAsync(ContentQuery query, CancellationToken cancellationToken)
    {
        ContentPage page = await store.GetArticlesAsync(query, cancellationToken);
        return new PagedArticleResponse(
            [.. page.Items.Select(ToSummary)],
            page.TotalItems,
            query.Page,
            query.PageSize);
    }

    private static ArticleSummary ToSummary(ContentArticle article)
        => new(
            article.Item.PublicId.ToString(),
            article.Item.Slug,
            article.Item.Kind.ToString(),
            article.Item.Title,
            article.Item.Excerpt,
            article.Item.Body,
            MediaUrl(article.HeroStorageKey),
            [.. article.Categories.Select(bucket => new ContentCategoryRef(bucket.Slug, bucket.Name))],
            [.. article.Tags.Select(tag => new ContentTagRef(tag.Slug, tag.Name))],
            article.Item.PublishedAt);

    /// <summary>
    /// The public URL of an ingested asset. Null in, null out: an article whose
    /// artwork has not been ingested renders without an image rather than with
    /// an invented or hotlinked one (api.md §3.5).
    /// </summary>
    private static string? MediaUrl(string? storageKey)
        => string.IsNullOrWhiteSpace(storageKey) ? null : $"/api/v1/catalog/media/{storageKey}";

    /// <summary>
    /// Locale falls back to English. The reference publishes both locales from
    /// the same slug, so an unrecognised value must not become a 500 or an
    /// empty page — it resolves to the default content locale.
    /// </summary>
    private static string NormaliseLocale(string? locale)
        => locale is not null && SupportedLocales.Contains(locale) ? locale : "en";

    private static int ClampPageSize(int pageSize)
        => pageSize <= 0 ? DefaultPageSize : Math.Min(pageSize, MaxPageSize);

    private static string? Trim(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
