using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Stockpile.Domain.Entities.Catalogue;
using Stockpile.Domain.Entities.Identity;
using Stockpile.Domain.Entities.Procurement;
using Stockpile.Domain.Enums;
using static Stockpile.Infrastructure.Persistence.ModelBuilderExtensions;

namespace Stockpile.Infrastructure.Persistence.Configurations;

/// <summary>
/// Procurement aggregate configuration (domain-model §5.3–§5.5). Enum columns
/// are nvarchar(40) + CHECK via <see cref="EnumIn{TEnum}"/>; child rows cascade
/// from the RFQ, while every cross-aggregate reference (organisation, category,
/// product, unit, supplier) is <c>Restrict</c> so an RFQ can never be reshaped
/// by deleting something else.
/// </summary>
public sealed class RfqConfiguration : IEntityTypeConfiguration<Rfq>
{
    public void Configure(EntityTypeBuilder<Rfq> builder)
    {
        builder.ToTable("RFQs", t =>
        {
            t.HasCheckConstraint("CK_RFQs_Status", EnumIn<RFQStatus>("Status"));
            t.HasCheckConstraint("CK_RFQs_Visibility", EnumIn<RfqVisibility>("Visibility"));
            t.HasCheckConstraint("CK_RFQs_Source", EnumIn<RfqSource>("Source"));
        });

        builder.HasKey(x => x.PublicId);
        builder.Property(x => x.PublicId).ValueGeneratedNever(); // Guid v4, code-assigned
        builder.Property(x => x.ReferenceCode).HasMaxLength(30).IsRequired();
        builder.Property(x => x.Title).HasMaxLength(300).IsRequired();
        builder.Property(x => x.DeliveryLocationText).HasMaxLength(300);
        builder.Property(x => x.BudgetAmount).HasPrecision(18, 4);
        builder.Property(x => x.CurrencyCode).HasMaxLength(3);
        builder.Property(x => x.SiteConditions).HasMaxLength(500);
        builder.Property(x => x.SpecialInstructions).HasMaxLength(2000);
        builder.Property(x => x.CancelReason).HasMaxLength(400);
        builder.Property(x => x.Status).HasEnumString();
        builder.Property(x => x.Visibility).HasEnumString();
        builder.Property(x => x.Source).HasEnumString();
        builder.Property(x => x.RowVersion).IsRowVersion();

        builder.HasIndex(x => x.ReferenceCode).IsUnique();
        builder.HasIndex(x => new { x.OrganizationId, x.Status });

        builder.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(x => x.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class RfqItemConfiguration : IEntityTypeConfiguration<RfqItem>
{
    public void Configure(EntityTypeBuilder<RfqItem> builder)
    {
        builder.ToTable("RFQItems");

        builder.HasKey(x => x.PublicId);
        builder.Property(x => x.PublicId).ValueGeneratedNever();

        builder.Property(x => x.Description).HasMaxLength(500).IsRequired();
        builder.Property(x => x.Specification).HasMaxLength(500);
        builder.Property(x => x.Notes).HasMaxLength(500);
        builder.Property(x => x.Quantity).HasPrecision(18, 4);

        builder.HasIndex(x => new { x.RfqId, x.LineNumber });

        builder.HasOne<Rfq>()
            .WithMany()
            .HasForeignKey(x => x.RfqId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Product>()
            .WithMany()
            .HasForeignKey(x => x.ProductId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Category>()
            .WithMany()
            .HasForeignKey(x => x.CategoryId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<UnitOfMeasure>()
            .WithMany()
            .HasForeignKey(x => x.UnitOfMeasureId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class RfqMatchConfiguration : IEntityTypeConfiguration<RfqMatch>
{
    public void Configure(EntityTypeBuilder<RfqMatch> builder)
    {
        builder.ToTable("RFQMatches", t =>
            t.HasCheckConstraint("CK_RFQMatches_Status", EnumIn<RFQMatchStatus>("Status")));

        builder.HasKey(x => x.PublicId);
        builder.Property(x => x.PublicId).ValueGeneratedNever();

        builder.Property(x => x.Status).HasEnumString();
        builder.Property(x => x.MatchScore).HasPrecision(9, 4);
        builder.Property(x => x.MatchReasons).HasMaxLength(400).IsRequired();
        builder.Property(x => x.DeclineReason).HasMaxLength(400);

        // One match per supplier per RFQ (domain-model §5.4).
        builder.HasIndex(x => new { x.RfqId, x.SupplierId }).IsUnique();
        builder.HasIndex(x => x.SupplierId);

        builder.HasOne<Rfq>()
            .WithMany()
            .HasForeignKey(x => x.RfqId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Supplier>()
            .WithMany()
            .HasForeignKey(x => x.SupplierId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class RfqStatusHistoryConfiguration : IEntityTypeConfiguration<RfqStatusHistory>
{
    public void Configure(EntityTypeBuilder<RfqStatusHistory> builder)
    {
        builder.ToTable("RFQStatusHistory", t =>
        {
            t.HasCheckConstraint("CK_RFQStatusHistory_FromState", EnumIn<RFQStatus>("FromState"));
            t.HasCheckConstraint("CK_RFQStatusHistory_ToState", EnumIn<RFQStatus>("ToState"));
        });

        builder.HasKey(x => x.PublicId);
        builder.Property(x => x.PublicId).ValueGeneratedNever();

        builder.Property(x => x.FromState).HasEnumString();
        builder.Property(x => x.ToState).HasEnumString();
        builder.Property(x => x.Reason).HasMaxLength(400);
        builder.Property(x => x.CorrelationId).HasMaxLength(100);

        builder.HasIndex(x => new { x.RfqId, x.OccurredAt });

        builder.HasOne<Rfq>()
            .WithMany()
            .HasForeignKey(x => x.RfqId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
