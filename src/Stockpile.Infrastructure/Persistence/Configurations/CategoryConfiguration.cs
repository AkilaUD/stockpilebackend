using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Stockpile.Domain.Entities.Catalogue;
using Stockpile.Domain.Enums;
using static Stockpile.Infrastructure.Persistence.ModelBuilderExtensions;

namespace Stockpile.Infrastructure.Persistence.Configurations;

public sealed class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    public void Configure(EntityTypeBuilder<Category> builder)
    {
        builder.ToTable("Categories");

        builder.HasKey(x => x.PublicId);
        builder.Property(x => x.PublicId).ValueGeneratedNever(); // Guid v7, code-assigned

        builder.Property(x => x.Slug).HasMaxLength(160).IsRequired();
        builder.Property(x => x.Name).HasMaxLength(160).IsRequired();
        builder.Property(x => x.NameSi).HasMaxLength(160);
        builder.Property(x => x.Description).HasMaxLength(2000);
        builder.Property(x => x.IconKey).HasMaxLength(40);
        builder.Property(x => x.MaterializedPath).HasMaxLength(400).IsRequired();

        builder.HasIndex(x => x.Slug).IsUnique();
        builder.HasIndex(x => x.ParentId);
        builder.HasIndex(x => x.MaterializedPath);

        // Tree: self-referencing; deleting a node is blocked while children exist.
        builder.HasOne<Category>()
            .WithMany()
            .HasForeignKey(x => x.ParentId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class AttributeDefinitionConfiguration : IEntityTypeConfiguration<AttributeDefinition>
{
    public void Configure(EntityTypeBuilder<AttributeDefinition> builder)
    {
        builder.ToTable("AttributeDefinitions");

        builder.HasKey(x => x.PublicId);
        builder.Property(x => x.PublicId).ValueGeneratedNever();

        builder.Property(x => x.Code).HasMaxLength(80).IsRequired();
        builder.Property(x => x.DisplayName).HasMaxLength(120).IsRequired();
        builder.Property(x => x.DataType).HasEnumString();
        builder.Property(x => x.HelpText).HasMaxLength(500);
        builder.Property(x => x.DisplayFormat).HasMaxLength(40);

        builder.HasIndex(x => x.Code).IsUnique();

        builder.HasOne<UnitOfMeasure>()
            .WithMany()
            .HasForeignKey(x => x.UnitOfMeasureId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

public sealed class AttributeOptionConfiguration : IEntityTypeConfiguration<AttributeOption>
{
    public void Configure(EntityTypeBuilder<AttributeOption> builder)
    {
        builder.ToTable("AttributeOptions");

        builder.HasKey(x => x.PublicId);
        builder.Property(x => x.PublicId).ValueGeneratedNever();

        builder.Property(x => x.Code).HasMaxLength(80).IsRequired();
        builder.Property(x => x.DisplayName).HasMaxLength(120).IsRequired();
        builder.Property(x => x.Swatch).HasMaxLength(9);

        builder.HasIndex(x => new { x.AttributeId, x.Code }).IsUnique();

        builder.HasOne<AttributeDefinition>()
            .WithMany()
            .HasForeignKey(x => x.AttributeId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class CategoryAttributeDefinitionConfiguration : IEntityTypeConfiguration<CategoryAttributeDefinition>
{
    public void Configure(EntityTypeBuilder<CategoryAttributeDefinition> builder)
    {
        builder.ToTable("CategoryAttributeDefinitions");

        builder.HasKey(x => new { x.CategoryId, x.AttributeId });
        builder.Property(x => x.DefaultValue).HasMaxLength(200);
        builder.Property(x => x.AppliesToItemType).HasEnumString();

        builder.HasOne<Category>()
            .WithMany()
            .HasForeignKey(x => x.CategoryId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<AttributeDefinition>()
            .WithMany()
            .HasForeignKey(x => x.AttributeId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}