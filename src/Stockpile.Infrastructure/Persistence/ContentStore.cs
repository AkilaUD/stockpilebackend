using Microsoft.EntityFrameworkCore;
using Stockpile.Application.Content;
using Stockpile.Domain.Entities.Content;
using Stockpile.Domain.Enums;

namespace Stockpile.Infrastructure.Persistence;

/// <summary>
/// EF Core implementation of the content port (architecture §8.5 module
/// "content"). Reads are no-tracking and compose the reference values the
/// responses need — bucket names, tag names and the hero asset's storage key —
/// so the Application layer never re-joins. Only <c>Published</c> rows are
/// visible: a Draft or Archived article must not be reachable by slug.
/// </summary>
public sealed class ContentStore(StockpileDbContext db) : IContentStore
{
    /// <summary>The content locale the reference actually publishes in.</summary>
    private const string DefaultLocale = "en";

    /// <inheritdoc />
    public async Task<ContentPage> GetArticlesAsync(ContentQuery query, CancellationToken cancellationToken)
    {
        string locale = await ResolveLocaleAsync(query.Locale, cancellationToken);

        IQueryable<ContentItem> items = db.ContentItems
            .AsNoTracking()
            .Where(item => item.Status == ContentStatus.Published && item.Locale == locale);

        if (query.Kinds is { } kinds)
        {
            // An empty list is a filter that matches nothing (see ContentQuery):
            // it is answered here rather than short-circuited, so the paging
            // envelope stays uniform.
            items = kinds.Count == 0
                ? items.Where(item => false)
                : items.Where(item => kinds.Contains(item.Kind));
        }

        if (query.TagSlug is string tagSlug)
        {
            Guid? tagId = await db.ContentTags
                .Where(tag => tag.Slug == tagSlug)
                .Select(tag => (Guid?)tag.PublicId)
                .FirstOrDefaultAsync(cancellationToken);
            if (tagId is null)
            {
                return new ContentPage([], 0);
            }

            items = items.Where(item => db.ContentItemTags
                .Any(link => link.ContentId == item.PublicId && link.TagId == tagId.Value));
        }

        if (query.CategorySlug is string categorySlug)
        {
            Guid? categoryId = await db.ContentCategories
                .Where(category => category.Slug == categorySlug && category.IsActive)
                .Select(category => (Guid?)category.PublicId)
                .FirstOrDefaultAsync(cancellationToken);
            if (categoryId is null)
            {
                return new ContentPage([], 0);
            }

            items = items.Where(item => db.ContentItemCategories
                .Any(link => link.ContentId == item.PublicId && link.CategoryId == categoryId.Value));
        }

        int total = await items.CountAsync(cancellationToken);
        if (total == 0)
        {
            return new ContentPage([], 0);
        }

        List<ContentItem> rows = await items
            .OrderByDescending(item => item.PublishedAt)
            .ThenBy(item => item.Title)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);

        return new ContentPage(await ComposeAsync(rows, cancellationToken), total);
    }

    /// <inheritdoc />
    public async Task<ContentArticle?> GetArticleAsync(
        string slug, string locale, CancellationToken cancellationToken)
    {
        ContentItem? item = await db.ContentItems
            .AsNoTracking()
            .FirstOrDefaultAsync(
                row => row.Slug == slug && row.Locale == locale && row.Status == ContentStatus.Published,
                cancellationToken);

        // The reference's Sinhala store view serves the English article — only
        // the chrome around it is translated (verified against
        // /si/news/post/{slug}, 2026-10-14). An untranslated locale therefore
        // reads the default content locale rather than 404-ing; a real
        // translation row, once written, always wins.
        if (item is null && !string.Equals(locale, DefaultLocale, StringComparison.Ordinal))
        {
            item = await db.ContentItems
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    row => row.Slug == slug && row.Locale == DefaultLocale && row.Status == ContentStatus.Published,
                    cancellationToken);
        }

        if (item is null)
        {
            return null;
        }

        return (await ComposeAsync([item], cancellationToken))[0];
    }

    /// <summary>
    /// Resolves the locale a list read should use: the requested one when it has
    /// published rows, otherwise the default. Same rationale as the article
    /// fallback above — see it for the evidence.
    /// </summary>
    private async Task<string> ResolveLocaleAsync(string locale, CancellationToken cancellationToken)
    {
        if (string.Equals(locale, DefaultLocale, StringComparison.Ordinal))
        {
            return DefaultLocale;
        }

        bool translated = await db.ContentItems
            .AsNoTracking()
            .AnyAsync(
                item => item.Locale == locale && item.Status == ContentStatus.Published,
                cancellationToken);
        return translated ? locale : DefaultLocale;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ContentBucket>> GetCategoriesAsync(CancellationToken cancellationToken)
    {
        List<ContentCategory> rows = await db.ContentCategories
            .AsNoTracking()
            .Where(category => category.IsActive)
            .OrderBy(category => category.SortOrder)
            .ThenBy(category => category.Name)
            .ToListAsync(cancellationToken);

        return [.. rows.Select(row => new ContentBucket(row.Slug, row.Name))];
    }

    /// <summary>
    /// Attaches buckets, tag names and the hero asset's storage key to a set of
    /// rows in four batched reads — never a query per row.
    /// </summary>
    private async Task<List<ContentArticle>> ComposeAsync(
        List<ContentItem> rows, CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        List<Guid> contentIds = [.. rows.Select(row => row.PublicId)];

        List<ContentItemCategory> links = await db.ContentItemCategories
            .AsNoTracking()
            .Where(link => contentIds.Contains(link.ContentId))
            .ToListAsync(cancellationToken);

        List<ContentItemTag> tagLinks = await db.ContentItemTags
            .AsNoTracking()
            .Where(link => contentIds.Contains(link.ContentId))
            .ToListAsync(cancellationToken);

        List<Guid> categoryIds = [.. links.Select(link => link.CategoryId).Distinct()];
        List<Guid> tagIds = [.. tagLinks.Select(link => link.TagId).Distinct()];
        List<Guid> mediaIds = [.. rows
            .Where(row => row.HeroImageMediaId.HasValue)
            .Select(row => row.HeroImageMediaId!.Value)
            .Distinct()];

        Dictionary<Guid, ContentBucket> buckets = categoryIds.Count == 0
            ? new Dictionary<Guid, ContentBucket>()
            : (await db.ContentCategories
                    .AsNoTracking()
                    .Where(category => categoryIds.Contains(category.PublicId))
                    .ToListAsync(cancellationToken))
                .ToDictionary(category => category.PublicId, category => new ContentBucket(category.Slug, category.Name));

        Dictionary<Guid, ContentTagLink> tags = tagIds.Count == 0
            ? new Dictionary<Guid, ContentTagLink>()
            : (await db.ContentTags
                    .AsNoTracking()
                    .Where(tag => tagIds.Contains(tag.PublicId))
                    .ToListAsync(cancellationToken))
                .ToDictionary(tag => tag.PublicId, tag => new ContentTagLink(tag.Slug, tag.Name));

        Dictionary<Guid, string> heroKeys = mediaIds.Count == 0
            ? new Dictionary<Guid, string>()
            : (await db.MediaAssets
                    .AsNoTracking()
                    .Where(asset => mediaIds.Contains(asset.PublicId))
                    .ToListAsync(cancellationToken))
                .ToDictionary(asset => asset.PublicId, asset => asset.StorageKey);

        return
        [
            .. rows.Select(row => new ContentArticle(
                row,
                [.. links
                    .Where(link => link.ContentId == row.PublicId)
                    .Select(link => buckets.GetValueOrDefault(link.CategoryId))
                    .OfType<ContentBucket>()
                    .OrderBy(bucket => bucket.Name, StringComparer.Ordinal)],
                [.. tagLinks
                    .Where(link => link.ContentId == row.PublicId)
                    .Select(link => tags.GetValueOrDefault(link.TagId))
                    .OfType<ContentTagLink>()
                    .OrderBy(tag => tag.Name, StringComparer.Ordinal)],
                row.HeroImageMediaId is Guid mediaId ? heroKeys.GetValueOrDefault(mediaId) : null)),
        ];
    }
}
