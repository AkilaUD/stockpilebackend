using Stockpile.Domain.Common;
using Stockpile.Domain.Enums;

namespace Stockpile.Domain.Entities.Content;

/// <summary>
/// Aggregate root (domain-model §9): one published piece of editorial content —
/// the news tree, completed-project write-ups, supplier stories and guides all
/// share this shape. Content is <b>per-locale rows</b>, not inline translation:
/// the reference serves <c>/en/news/post/{slug}</c> and <c>/si/news/post/{slug}</c>
/// from the same slug, so uniqueness is declared over (Locale, Slug) rather than
/// over Slug alone.
/// <para>
/// <see cref="Body"/> is sanitized HTML with a trusted allowlist at write time
/// (§13.9) — the seeded copy is the reference's own markup, and no reader ever
/// renders it unsanitized.
/// </para>
/// </summary>
public sealed class ContentItem : AuditableEntity
{
    public Guid PublicId { get; set; } = Guid.CreateVersion7();

    public ContentKind Kind { get; set; } = ContentKind.NewsArticle;

    public string Title { get; set; } = string.Empty;

    /// <summary>URL-safe slug, unique per locale (§9).</summary>
    public string Slug { get; set; } = string.Empty;

    public string? Excerpt { get; set; }

    /// <summary>Rich text as sanitized HTML (§13.9). Never empty for a published row.</summary>
    public string Body { get; set; } = string.Empty;

    public Guid? HeroImageMediaId { get; set; }

    public Guid? AuthorUserId { get; set; }

    public Guid? EditorUserId { get; set; }

    public ContentStatus Status { get; set; } = ContentStatus.Published;

    public DateTime? PublishedAt { get; set; }

    public DateTime? ScheduledAt { get; set; }

    public string? MetaTitle { get; set; }

    public string? MetaDescription { get; set; }

    public string? CanonicalUrl { get; set; }

    /// <summary>ISO 639-1 code of this row, e.g. <c>en</c> or <c>si</c> (§9).</summary>
    public string Locale { get; set; } = "en";
}

/// <summary>The reference's news buckets (Completed Jobs, Features, Stockpile, Suppliers).</summary>
public sealed class ContentCategory
{
    public Guid PublicId { get; set; } = Guid.CreateVersion7();

    public string Slug { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>Sinhala name where the reference publishes one.</summary>
    public string? NameSi { get; set; }

    public string? Description { get; set; }

    public int SortOrder { get; set; }

    public bool IsActive { get; set; } = true;
}

/// <summary>Content ↔ category join (domain-model §9).</summary>
public sealed class ContentItemCategory
{
    public Guid ContentId { get; set; }

    public Guid CategoryId { get; set; }
}

/// <summary>Editorial tag (domain-model §9), used by the <c>?tag=</c> filter.</summary>
public sealed class ContentTag
{
    public Guid PublicId { get; set; } = Guid.CreateVersion7();

    public string Slug { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
}

/// <summary>Content ↔ tag join (domain-model §9).</summary>
public sealed class ContentItemTag
{
    public Guid ContentId { get; set; }

    public Guid TagId { get; set; }
}
