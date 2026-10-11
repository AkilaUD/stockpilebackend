using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Stockpile.Domain.Entities.Catalogue;
using Stockpile.Domain.Entities.Media;
using Stockpile.Domain.Enums;
using static Stockpile.Infrastructure.Persistence.ModelBuilderExtensions;

namespace Stockpile.Infrastructure.Persistence.Configurations;

public sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.ToTable("Products", t =>
            t.HasCheckConstraint("CK_Products_Status", EnumIn<ProductStatus>("Status")));

        builder.HasKey(x => x.PublicId);
        builder.Property(x => x.PublicId).ValueGeneratedNever(); // Guid v7, code-assigned

        builder.Property(x => x.Name).HasMaxLength(300).IsRequired();
        builder.Property(x => x.Slug).HasMaxLength(300).IsRequired();
        builder.Property(x => x.Sku).HasMaxLength(100).IsRequired();
        builder.Property(x => x.Summary).HasMaxLength(500);
        builder.Property(x => x.Description).HasMaxLength(8000);
        builder.Property(x => x.ItemType).HasEnumString();
        builder.Property(x => x.Status).HasEnumString();
        builder.Property(x => x.CountryOfOriginCode).HasMaxLength(2);
        builder.Property(x => x.TaxClassCode).HasMaxLength(40);
        builder.Property(x => x.MinOrderQuantity).HasPrecision(18, 2);
        builder.Property(x => x.RowVersion).IsRowVersion();

        builder.HasIndex(x => x.Slug).IsUnique();
        builder.HasIndex(x => new { x.SupplierId, x.Sku }).IsUnique();
        builder.HasIndex(x => x.CategoryId);
        builder.HasIndex(x => x.Status);

        builder.HasOne<Supplier>()
            .WithMany()
            .HasForeignKey(x => x.SupplierId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Category>()
            .WithMany()
            .HasForeignKey(x => x.CategoryId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Brand>()
            .WithMany()
            .HasForeignKey(x => x.BrandId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne<Manufacturer>()
            .WithMany()
            .HasForeignKey(x => x.ManufacturerId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

public sealed class ProductAttributeValueConfiguration : IEntityTypeConfiguration<ProductAttributeValue>
{
    public void Configure(EntityTypeBuilder<ProductAttributeValue> builder)
    {
        builder.ToTable("ProductAttributeValues");

        builder.HasKey(x => x.PublicId);
        builder.Property(x => x.PublicId).ValueGeneratedNever();

        builder.Property(x => x.ValueText).HasMaxLength(600);
        builder.Property(x => x.ValueNumber).HasPrecision(18, 4);

        // Exactly one value slot carries data; enforced by the unique definition
        // constraint and validated in the write path (domain-model §4.2).
        builder.HasIndex(x => new { x.ProductId, x.AttributeId }).IsUnique();

        builder.HasOne<Product>()
            .WithMany()
            .HasForeignKey(x => x.ProductId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<AttributeDefinition>()
            .WithMany()
            .HasForeignKey(x => x.AttributeId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<AttributeOption>()
            .WithMany()
            .HasForeignKey(x => x.OptionId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class ProductPriceConfiguration : IEntityTypeConfiguration<ProductPrice>
{
    public void Configure(EntityTypeBuilder<ProductPrice> builder)
    {
        builder.ToTable("ProductPrices", t =>
            t.HasCheckConstraint("CK_ProductPrices_PriceState", EnumIn<PriceState>("PriceState")));

        builder.HasKey(x => x.PublicId);
        builder.Property(x => x.PublicId).ValueGeneratedNever();

        builder.Property(x => x.CurrencyCode).HasMaxLength(3).IsRequired();
        builder.Property(x => x.Amount).HasPrecision(18, 2).IsRequired();
        builder.Property(x => x.PriceState).HasEnumString();
        builder.Property(x => x.MinQty).HasPrecision(18, 2);
        builder.Property(x => x.MaxQty).HasPrecision(18, 2);

        builder.HasIndex(x => new { x.ProductId, x.CurrencyCode }).IsUnique();
        builder.HasIndex(x => x.ProductId);

        builder.HasOne<Product>()
            .WithMany()
            .HasForeignKey(x => x.ProductId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class ProductPriceHistoryConfiguration : IEntityTypeConfiguration<ProductPriceHistory>
{
    public void Configure(EntityTypeBuilder<ProductPriceHistory> builder)
    {
        builder.ToTable("ProductPriceHistory", t =>
            t.HasCheckConstraint("CK_ProductPriceHistory_NonNegativeChange",
                "[PreviousAmount] >= 0 AND [NewAmount] >= 0"));

        builder.HasKey(x => x.PublicId);
        builder.Property(x => x.PublicId).ValueGeneratedNever();

        builder.Property(x => x.CurrencyCode).HasMaxLength(3).IsRequired();
        builder.Property(x => x.PreviousAmount).HasPrecision(18, 2).IsRequired();
        builder.Property(x => x.NewAmount).HasPrecision(18, 2).IsRequired();
        builder.Property(x => x.Reason).HasMaxLength(200);

        builder.HasIndex(x => new { x.ProductId, x.ChangedAt });

        builder.HasOne<Product>()
            .WithMany()
            .HasForeignKey(x => x.ProductId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class ProductStockConfiguration : IEntityTypeConfiguration<ProductStock>
{
    public void Configure(EntityTypeBuilder<ProductStock> builder)
    {
        builder.ToTable("ProductStocks", t =>
        {
            t.HasCheckConstraint("CK_ProductStocks_StockState", EnumIn<StockState>("StockState"));
            t.HasCheckConstraint("CK_ProductStocks_NonNegative", "[OnHand] >= 0 AND [Reserved] >= 0");
        });

        builder.HasKey(x => x.PublicId);
        builder.Property(x => x.PublicId).ValueGeneratedNever();

        builder.Property(x => x.StockState).HasEnumString();
        builder.Property(x => x.RowVersion).IsRowVersion();

        builder.HasIndex(x => x.ProductId).IsUnique();

        builder.HasOne<Product>()
            .WithMany()
            .HasForeignKey(x => x.ProductId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class StockAdjustmentConfiguration : IEntityTypeConfiguration<StockAdjustment>
{
    public void Configure(EntityTypeBuilder<StockAdjustment> builder)
    {
        builder.ToTable("StockAdjustments");

        builder.HasKey(x => x.PublicId);
        builder.Property(x => x.PublicId).ValueGeneratedNever();

        builder.Property(x => x.Reason).HasMaxLength(200);
        builder.Property(x => x.ReferenceType).HasMaxLength(40);

        builder.HasIndex(x => new { x.ProductId, x.OccurredAt });

        builder.HasOne<Product>()
            .WithMany()
            .HasForeignKey(x => x.ProductId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class ProductMediaConfiguration : IEntityTypeConfiguration<ProductMedia>
{
    public void Configure(EntityTypeBuilder<ProductMedia> builder)
    {
        builder.ToTable("ProductMedia");

        builder.HasKey(x => x.PublicId);
        builder.Property(x => x.PublicId).ValueGeneratedNever();

        builder.Property(x => x.AltText).HasMaxLength(300);

        builder.HasIndex(x => new { x.ProductId, x.SortOrder });

        builder.HasOne<Product>()
            .WithMany()
            .HasForeignKey(x => x.ProductId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<MediaAsset>()
            .WithMany()
            .HasForeignKey(x => x.MediaAssetId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}