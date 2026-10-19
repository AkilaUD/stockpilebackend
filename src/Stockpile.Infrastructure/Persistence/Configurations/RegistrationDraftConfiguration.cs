using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Stockpile.Domain.Entities.Identity;

namespace Stockpile.Infrastructure.Persistence.Configurations;

/// <summary>
/// The registration-draft ledger (business-flows §2/§3, api.md §2.1). The
/// anonymous save/resume endpoints probe by hash ALONE — the token identifies its
/// draft — so the hash carries a unique index for a single-key lookup, exactly
/// like the verification/reset ledgers.
/// </summary>
public sealed class RegistrationDraftConfiguration : IEntityTypeConfiguration<RegistrationDraft>
{
    public void Configure(EntityTypeBuilder<RegistrationDraft> builder)
    {
        builder.ToTable("RegistrationDrafts");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever(); // Guid v4, code-assigned (X8)

        builder.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Kind).HasMaxLength(16).IsRequired();

        // Bounds mirror the validator in RegistrationDraftService; the column is
        // the last line, never the first.
        builder.Property(x => x.Email).HasMaxLength(320);
        builder.Property(x => x.FirstName).HasMaxLength(100);
        builder.Property(x => x.LastName).HasMaxLength(100);
        builder.Property(x => x.Phone).HasMaxLength(32);
        builder.Property(x => x.CompanyName).HasMaxLength(200);
        builder.Property(x => x.RegistrationNumber).HasMaxLength(64);
        builder.Property(x => x.Website).HasMaxLength(300);

        builder.HasIndex(x => x.TokenHash).IsUnique();

        // The opportunistic sweep on create deletes by expiry.
        builder.HasIndex(x => x.ExpiresAtUtc);

        // Nullable until the draft completes, then pointed at the account it became.
        // Restrict: a user row can never cascade a draft away (drafts die by expiry).
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(x => x.Categories)
            .WithOne()
            .HasForeignKey(x => x.DraftId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>One ticked category of a draft; unique per (draft, slug).</summary>
public sealed class RegistrationDraftCategoryConfiguration : IEntityTypeConfiguration<RegistrationDraftCategory>
{
    public void Configure(EntityTypeBuilder<RegistrationDraftCategory> builder)
    {
        builder.ToTable("RegistrationDraftCategories");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();

        builder.Property(x => x.Slug).HasMaxLength(100).IsRequired();

        builder.HasIndex(x => new { x.DraftId, x.Slug }).IsUnique();
    }
}
