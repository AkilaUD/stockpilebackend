using Stockpile.Domain.Entities.Content;
using Stockpile.Domain.Enums;

namespace Stockpile.Application.Content;

/// <summary>
/// Content data port (architecture §8.5 module "content"). Reads only — the
/// public surface never writes content; editorial authoring arrives with the
/// admin content endpoints (api.md §2.13). Rows come back composed with the
/// reference values their responses need (bucket names, tag names, hero media
/// key) so the service never re-joins.
/// </summary>
public interface IContentStore
{
    /// <summary>Published rows for a filter, newest first, with the exact total.</summary>
    Task<ContentPage> GetArticlesAsync(ContentQuery query, CancellationToken cancellationToken);

    /// <summary>One published row by slug in a locale, or null when unknown.</summary>
    Task<ContentArticle?> GetArticleAsync(string slug, string locale, CancellationToken cancellationToken);

    /// <summary>Active editorial buckets, in the reference's order.</summary>
    Task<IReadOnlyList<ContentBucket>> GetCategoriesAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The public list filter (api.md §2.12: <c>kind</c>, <c>tag</c>, <c>page</c>).
/// <paramref name="CategorySlug"/> is the surface-level extra the news tree
/// needs (<c>/news/category/{slug}</c>); passing null means "every bucket".
/// <para>
/// <paramref name="Kinds"/> is tri-state on purpose: <c>null</c> means "any
/// kind", a non-empty list means "one of these", and an <b>empty</b> list means
/// "matches nothing" — which is how an unrecognised <c>kind=</c> value is
/// answered. Silently dropping an unknown filter would return the whole corpus
/// for a typo, which reads as a working filter that is not filtering.
/// </para>
/// </summary>
public sealed record ContentQuery(
    IReadOnlyList<ContentKind>? Kinds,
    string? CategorySlug,
    string? TagSlug,
    string Locale,
    int Page,
    int PageSize);

/// <summary>One page of content rows plus the exact total (never an estimate).</summary>
public sealed record ContentPage(IReadOnlyList<ContentArticle> Items, int TotalItems);

/// <summary>A content row with its resolved buckets, tags and hero media key.</summary>
public sealed record ContentArticle(
    ContentItem Item,
    IReadOnlyList<ContentBucket> Categories,
    IReadOnlyList<ContentTagLink> Tags,
    string? HeroStorageKey);

/// <summary>A bucket as stored (slug + display name).</summary>
public sealed record ContentBucket(string Slug, string Name);

/// <summary>
/// A tag as stored (slug + display name). Distinct from the domain entity of
/// the same concept: the public read needs the link target, not the row.
/// </summary>
public sealed record ContentTagLink(string Slug, string Name);
