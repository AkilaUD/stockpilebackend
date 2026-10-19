using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Stockpile.Domain.Entities.Content;
using Stockpile.Domain.Entities.Media;
using Stockpile.Domain.Enums;
using static Stockpile.Infrastructure.Persistence.ModelBuilderExtensions;

namespace Stockpile.Infrastructure.Persistence.Configurations;

/// <summary>
/// Content aggregate configuration (domain-model §9). Enums are nvarchar(40) +
/// CHECK like every other aggregate. The unique index is over
/// <b>(Locale, Slug)</b>, not Slug alone: the reference serves the same slug
/// under both <c>/en/</c> and <c>/si/</c> from per-locale rows, so slug-only
/// uniqueness would make the second locale impossible. <c>Body</c> is
/// nvarchar(max) — it holds sanitized HTML (§13.9) that must never be truncated.
/// </summary>
public sealed class ContentConfiguration : IEntityTypeConfiguration<ContentItem>
{
    public void Configure(EntityTypeBuilder<ContentItem> builder)
    {
        builder.ToTable("ContentItems", t =>
        {
            t.HasCheckConstraint("CK_ContentItems_Kind", EnumIn<ContentKind>("Kind"));
            t.HasCheckConstraint("CK_ContentItems_Status", EnumIn<ContentStatus>("Status"));
        });

        builder.HasKey(x => x.PublicId);
        builder.Property(x => x.PublicId).ValueGeneratedNever(); // Guid v7, code-assigned

        builder.Property(x => x.Kind).HasEnumString();
        builder.Property(x => x.Status).HasEnumString();
        builder.Property(x => x.Title).HasMaxLength(300).IsRequired();
        builder.Property(x => x.Slug).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Excerpt).HasMaxLength(1000);
        builder.Property(x => x.Body).IsRequired();
        builder.Property(x => x.MetaTitle).HasMaxLength(200);
        builder.Property(x => x.MetaDescription).HasMaxLength(400);
        builder.Property(x => x.CanonicalUrl).HasMaxLength(400);
        builder.Property(x => x.Locale).HasMaxLength(5).IsRequired();

        builder.HasIndex(x => new { x.Locale, x.Slug }).IsUnique();
        builder.HasIndex(x => new { x.Status, x.Kind, x.PublishedAt });

        // Hero artwork lives in the media store; deleting an asset unlinks it
        // rather than deleting the article that referenced it.
        builder.HasOne<MediaAsset>()
            .WithMany()
            .HasForeignKey(x => x.HeroImageMediaId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

/// <summary>The reference's editorial buckets (news categories) — §9 <c>ContentCategory</c>.</summary>
public sealed class ContentCategoryConfiguration : IEntityTypeConfiguration<ContentCategory>
{
    public void Configure(EntityTypeBuilder<ContentCategory> builder)
    {
        builder.ToTable("ContentCategories");

        builder.HasKey(x => x.PublicId);
        builder.Property(x => x.PublicId).ValueGeneratedNever();

        builder.Property(x => x.Slug).HasMaxLength(160).IsRequired();
        builder.Property(x => x.Name).HasMaxLength(160).IsRequired();
        builder.Property(x => x.NameSi).HasMaxLength(160);
        builder.Property(x => x.Description).HasMaxLength(1000);

        builder.HasIndex(x => x.Slug).IsUnique();
    }
}

/// <summary>Content ↔ category join (§9).</summary>
public sealed class ContentItemCategoryConfiguration : IEntityTypeConfiguration<ContentItemCategory>
{
    public void Configure(EntityTypeBuilder<ContentItemCategory> builder)
    {
        builder.ToTable("ContentItemCategories");

        builder.HasKey(x => new { x.ContentId, x.CategoryId });

        builder.HasIndex(x => x.CategoryId);

        builder.HasOne<ContentItem>()
            .WithMany()
            .HasForeignKey(x => x.ContentId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<ContentCategory>()
            .WithMany()
            .HasForeignKey(x => x.CategoryId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>Editorial tag (§9).</summary>
public sealed class ContentTagConfiguration : IEntityTypeConfiguration<ContentTag>
{
    public void Configure(EntityTypeBuilder<ContentTag> builder)
    {
        builder.ToTable("ContentTags");

        builder.HasKey(x => x.PublicId);
        builder.Property(x => x.PublicId).ValueGeneratedNever();

        builder.Property(x => x.Slug).HasMaxLength(160).IsRequired();
        builder.Property(x => x.Name).HasMaxLength(160).IsRequired();

        builder.HasIndex(x => x.Slug).IsUnique();
    }
}

/// <summary>Content ↔ tag join (§9).</summary>
public sealed class ContentItemTagConfiguration : IEntityTypeConfiguration<ContentItemTag>
{
    public void Configure(EntityTypeBuilder<ContentItemTag> builder)
    {
        builder.ToTable("ContentItemTags");

        builder.HasKey(x => new { x.ContentId, x.TagId });

        builder.HasIndex(x => x.TagId);

        builder.HasOne<ContentItem>()
            .WithMany()
            .HasForeignKey(x => x.ContentId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<ContentTag>()
            .WithMany()
            .HasForeignKey(x => x.TagId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
