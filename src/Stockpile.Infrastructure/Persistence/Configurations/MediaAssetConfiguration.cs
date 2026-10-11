using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Stockpile.Domain.Entities.Identity;
using Stockpile.Domain.Entities.Media;
using Stockpile.Domain.Entities.Reference;
using Stockpile.Domain.Enums;
using static Stockpile.Infrastructure.Persistence.ModelBuilderExtensions;

namespace Stockpile.Infrastructure.Persistence.Configurations;

public sealed class MediaAssetConfiguration : IEntityTypeConfiguration<MediaAsset>
{
    public void Configure(EntityTypeBuilder<MediaAsset> builder)
    {
        builder.ToTable("MediaAssets", t =>
        {
            t.HasCheckConstraint("CK_MediaAssets_Status", EnumIn<MediaAssetStatus>("Status"));
            t.HasCheckConstraint("CK_MediaAssets_ByteSize", "[ByteSize] >= 0");
            t.HasCheckConstraint("CK_MediaAssets_Width", "[Width] IS NULL OR [Width] >= 0");
            t.HasCheckConstraint("CK_MediaAssets_Height", "[Height] IS NULL OR [Height] >= 0");
            t.HasCheckConstraint("CK_MediaAssets_FocalX", "[FocalX] IS NULL OR ([FocalX] >= 0 AND [FocalX] <= 1)");
            t.HasCheckConstraint("CK_MediaAssets_FocalY", "[FocalY] IS NULL OR ([FocalY] >= 0 AND [FocalY] <= 1)");
        });

        builder.HasKey(x => x.PublicId);
        builder.Property(x => x.PublicId).ValueGeneratedNever(); // Guid v7, code-assigned

        builder.Property(x => x.StorageKey).HasMaxLength(400).IsRequired();
        builder.Property(x => x.OriginalFileName).HasMaxLength(255).IsRequired();
        builder.Property(x => x.ContentType).HasMaxLength(120).IsRequired();
        builder.Property(x => x.Sha256).HasMaxLength(64).IsRequired();
        builder.Property(x => x.FocalX).HasPrecision(5, 4);
        builder.Property(x => x.FocalY).HasPrecision(5, 4);
        builder.Property(x => x.AltText).HasMaxLength(160);
        builder.Property(x => x.Caption).HasMaxLength(300);
        builder.Property(x => x.ProvenanceSourceUrl).HasMaxLength(500);
        builder.Property(x => x.ProvenanceAuthorisation).HasMaxLength(300);
        builder.Property(x => x.ProvenanceLicense).HasMaxLength(120);
        builder.Property(x => x.Status).HasEnumString();

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(x => x.UploadedBy)
            .OnDelete(DeleteBehavior.Restrict);

        // Content-addressed integrity + dedupe (§4.7), one asset per storage key.
        builder.HasIndex(x => x.Sha256).IsUnique().HasDatabaseName("UX_MediaAssets_Sha256");
        builder.HasIndex(x => x.StorageKey).IsUnique().HasDatabaseName("UX_MediaAssets_StorageKey");
        builder.HasIndex(x => new { x.Status, x.CreatedAt });
    }
}

public sealed class CountryConfiguration : IEntityTypeConfiguration<Country>
{
    public void Configure(EntityTypeBuilder<Country> builder)
    {
        builder.ToTable("Countries");
        builder.HasKey(x => x.Code);
        builder.Property(x => x.Code).HasMaxLength(2).ValueGeneratedNever();
        builder.Property(x => x.Name).HasMaxLength(100).IsRequired();
        builder.Property(x => x.NumericCode).HasMaxLength(3).IsRequired();
    }
}

public sealed class ProvinceConfiguration : IEntityTypeConfiguration<Province>
{
    public void Configure(EntityTypeBuilder<Province> builder)
    {
        builder.ToTable("Provinces");
        builder.HasKey(x => x.Code);
        builder.Property(x => x.Code).HasMaxLength(20).ValueGeneratedNever();
        builder.Property(x => x.Name).HasMaxLength(60).IsRequired();
    }
}

public sealed class DistrictConfiguration : IEntityTypeConfiguration<District>
{
    public void Configure(EntityTypeBuilder<District> builder)
    {
        builder.ToTable("Districts");
        builder.HasKey(x => x.Code);
        builder.Property(x => x.Code).HasMaxLength(20).ValueGeneratedNever();
        builder.Property(x => x.Name).HasMaxLength(60).IsRequired();
        builder.Property(x => x.ProvinceCode).HasMaxLength(20).IsRequired();

        builder.HasOne<Province>()
            .WithMany()
            .HasForeignKey(x => x.ProvinceCode)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
