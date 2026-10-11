using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Stockpile.Domain.Entities.Catalogue;
using Stockpile.Domain.Entities.Identity;
using Stockpile.Domain.Entities.Media;
using Stockpile.Domain.Entities.Reference;
using Stockpile.Domain.Enums;
using static Stockpile.Infrastructure.Persistence.ModelBuilderExtensions;

namespace Stockpile.Infrastructure.Persistence.Configurations;

public sealed class SupplierConfiguration : IEntityTypeConfiguration<Supplier>
{
    public void Configure(EntityTypeBuilder<Supplier> builder)
    {
        builder.ToTable("Suppliers", t =>
        {
            t.HasCheckConstraint("CK_Suppliers_Status", EnumIn<SupplierStatus>("Status"));
            t.HasCheckConstraint("CK_Suppliers_VerificationState", EnumIn<VerificationState>("VerificationState"));
        });

        builder.HasKey(x => x.PublicId);
        builder.Property(x => x.PublicId).ValueGeneratedNever(); // Guid v7, code-assigned

        builder.Property(x => x.Slug).HasMaxLength(160).IsRequired();
        builder.Property(x => x.DisplayName).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Status).HasEnumString();
        builder.Property(x => x.VerificationState).HasEnumString();
        builder.Property(x => x.Story).HasMaxLength(6000);
        builder.Property(x => x.EmployeeBand).HasMaxLength(30);
        builder.Property(x => x.DefaultAudiencePolicy).HasEnumString();
        builder.Property(x => x.RowVersion).IsRowVersion();

        builder.HasIndex(x => x.Slug).IsUnique();
        // No unique index on OrganizationId: external (pre-onboarding)
        // suppliers carry null. "One supplier per organisation" is enforced by
        // application logic when the org binding is written (migration era).
        builder.HasIndex(x => x.Status);

        builder.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(x => x.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);

        // Catalogue media is content-addressed and effectively immutable:
        // references use Restrict (NO ACTION), never SetNull/Cascade — two
        // SetNull FKs from Suppliers to MediaAssets (logo + banner) collide as
        // "multiple cascade paths" in SQL Server's graph check, and nothing in
        // the domain semantics wants a logo deleted by side effect.
        builder.HasOne<MediaAsset>()
            .WithMany()
            .HasForeignKey(x => x.LogoMediaId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<MediaAsset>()
            .WithMany()
            .HasForeignKey(x => x.BannerMediaId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(x => x.Profile)
            .WithOne()
            .HasForeignKey<SupplierProfile>(x => x.SupplierId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class SupplierProfileConfiguration : IEntityTypeConfiguration<SupplierProfile>
{
    public void Configure(EntityTypeBuilder<SupplierProfile> builder)
    {
        builder.ToTable("SupplierProfiles");

        builder.HasKey(x => x.SupplierId);

        builder.Property(x => x.ContactPerson).HasMaxLength(160);
        builder.Property(x => x.Phone).HasMaxLength(30);
        builder.Property(x => x.Email).HasMaxLength(160);
        builder.Property(x => x.Website).HasMaxLength(300);
        builder.Property(x => x.OperationAddress).HasMaxLength(300);
        builder.Property(x => x.CorporateAddress).HasMaxLength(300);
        builder.Property(x => x.Latitude).HasPrecision(9, 6);
        builder.Property(x => x.Longitude).HasPrecision(9, 6);
    }
}

public sealed class SupplierCategoryConfiguration : IEntityTypeConfiguration<SupplierCategory>
{
    public void Configure(EntityTypeBuilder<SupplierCategory> builder)
    {
        builder.ToTable("SupplierCategories", t =>
            t.HasCheckConstraint("CK_SupplierCategories_SpecialtyLevel", EnumIn<SpecialtyLevel>("SpecialtyLevel")));

        builder.HasKey(x => x.PublicId);
        builder.Property(x => x.PublicId).ValueGeneratedNever();

        builder.Property(x => x.SpecialtyLevel).HasEnumString();

        builder.HasIndex(x => new { x.SupplierId, x.CategoryId }).IsUnique();

        builder.HasOne<Supplier>()
            .WithMany()
            .HasForeignKey(x => x.SupplierId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Category>()
            .WithMany()
            .HasForeignKey(x => x.CategoryId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class SupplierAudiencePolicyConfiguration : IEntityTypeConfiguration<SupplierAudiencePolicy>
{
    public void Configure(EntityTypeBuilder<SupplierAudiencePolicy> builder)
    {
        builder.ToTable("SupplierAudiencePolicies", t =>
        {
            t.HasCheckConstraint("CK_SupplierAudiencePolicies_Audience", EnumIn<SupplierAudience>("Audience"));
            t.HasCheckConstraint("CK_SupplierAudiencePolicies_AppliesTo", EnumIn<AudienceAppliesTo>("AppliesTo"));
            t.HasCheckConstraint("CK_SupplierAudiencePolicies_Visibility", EnumIn<SupplierVisibility>("Visibility"));
        });

        builder.HasKey(x => x.SupplierId);
        builder.Property(x => x.Audience).HasEnumString();
        builder.Property(x => x.AppliesTo).HasEnumString();
        builder.Property(x => x.Visibility).HasEnumString();

        builder.HasOne<Supplier>()
            .WithOne()
            .HasForeignKey<SupplierAudiencePolicy>(x => x.SupplierId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class SupplierServiceAreaConfiguration : IEntityTypeConfiguration<SupplierServiceArea>
{
    public void Configure(EntityTypeBuilder<SupplierServiceArea> builder)
    {
        builder.ToTable("SupplierServiceAreas");

        builder.HasKey(x => x.PublicId);
        builder.Property(x => x.PublicId).ValueGeneratedNever();

        // Districts.Code is nvarchar(20); the FK column must match (SQL Server
        // refuses length-mismatched FK columns).
        builder.Property(x => x.DistrictCode).HasMaxLength(20).IsRequired();
        builder.Property(x => x.RadiusKm).HasPrecision(9, 2);

        builder.HasIndex(x => new { x.SupplierId, x.DistrictCode }).IsUnique();

        builder.HasOne<Supplier>()
            .WithMany()
            .HasForeignKey(x => x.SupplierId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<District>()
            .WithMany()
            .HasForeignKey(x => x.DistrictCode)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class SupplierVerificationConfiguration : IEntityTypeConfiguration<SupplierVerification>
{
    public void Configure(EntityTypeBuilder<SupplierVerification> builder)
    {
        builder.ToTable("SupplierVerifications", t =>
            t.HasCheckConstraint("CK_SupplierVerifications_Decision", EnumIn<VerificationDecision>("Decision")));

        builder.HasKey(x => x.PublicId);
        builder.Property(x => x.PublicId).ValueGeneratedNever();

        builder.Property(x => x.Decision).HasEnumString();
        builder.Property(x => x.Reason).HasMaxLength(400);

        builder.HasIndex(x => new { x.SupplierId, x.RequestedAt });

        builder.HasOne<Supplier>()
            .WithMany()
            .HasForeignKey(x => x.SupplierId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class SupplierBadgeConfiguration : IEntityTypeConfiguration<SupplierBadge>
{
    public void Configure(EntityTypeBuilder<SupplierBadge> builder)
    {
        builder.ToTable("SupplierBadges");

        builder.HasKey(x => x.PublicId);
        builder.Property(x => x.PublicId).ValueGeneratedNever();

        builder.Property(x => x.BadgeKey).HasMaxLength(40).IsRequired();
        builder.Property(x => x.Label).HasMaxLength(80);
        builder.Property(x => x.Reason).HasMaxLength(200);

        builder.HasIndex(x => new { x.SupplierId, x.BadgeKey }).IsUnique();

        builder.HasOne<Supplier>()
            .WithMany()
            .HasForeignKey(x => x.SupplierId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class SupplierPublicPageConfigConfiguration : IEntityTypeConfiguration<SupplierPublicPageConfig>
{
    public void Configure(EntityTypeBuilder<SupplierPublicPageConfig> builder)
    {
        builder.ToTable("SupplierPublicPageConfigs");

        builder.HasKey(x => x.SupplierId);

        builder.Property(x => x.HeroHeadline).HasMaxLength(200);
        builder.Property(x => x.PrimaryCtaLabel).HasMaxLength(60);
        builder.Property(x => x.CustomSections).HasMaxLength(8000);

        builder.HasOne<Supplier>()
            .WithOne()
            .HasForeignKey<SupplierPublicPageConfig>(x => x.SupplierId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}