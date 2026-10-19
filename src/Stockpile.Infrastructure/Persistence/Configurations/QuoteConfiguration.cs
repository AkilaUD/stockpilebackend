using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Stockpile.Domain.Entities.Catalogue;
using Stockpile.Domain.Entities.Procurement;
using Stockpile.Domain.Enums;
using static Stockpile.Infrastructure.Persistence.ModelBuilderExtensions;

namespace Stockpile.Infrastructure.Persistence.Configurations;

/// <summary>
/// Quotations aggregate configuration (domain-model §6.1–§6.2). Enum columns are
/// nvarchar(40) + CHECK via <see cref="EnumIn{TEnum}"/>; revisions and their items
/// cascade from the quote, while every cross-aggregate reference (RFQ, match,
/// supplier, RFQ line, product, unit) is <c>Restrict</c> — with the single
/// deliberate exception of the item's RFQ-line pointer, which is <c>SetNull</c>
/// because the snapshot must survive its source (see below).
/// </summary>
public sealed class QuoteConfiguration : IEntityTypeConfiguration<Quote>
{
    public void Configure(EntityTypeBuilder<Quote> builder)
    {
        builder.ToTable("Quotes", t =>
        {
            t.HasCheckConstraint("CK_Quotes_Status", EnumIn<QuoteStatus>("Status"));
        });

        builder.HasKey(x => x.PublicId);
        builder.Property(x => x.PublicId).ValueGeneratedNever(); // Guid v4, code-assigned
        builder.Property(x => x.QuoteNumber).HasMaxLength(30).IsRequired();
        builder.Property(x => x.Status).HasEnumString();
        builder.Property(x => x.DeclineReason).HasMaxLength(400);
        builder.Property(x => x.RowVersion).IsRowVersion();

        builder.HasIndex(x => x.QuoteNumber).IsUnique();
        builder.HasIndex(x => new { x.RfqId, x.SupplierId }).IsUnique(); // §6.1: a supplier quotes each RFQ once
        builder.HasIndex(x => x.SupplierId);

        builder.HasOne<Rfq>()
            .WithMany()
            .HasForeignKey(x => x.RfqId)
            .OnDelete(DeleteBehavior.Cascade); // a quote cannot outlive the RFQ it answers

        builder.HasOne<RfqMatch>()
            .WithMany()
            .HasForeignKey(x => x.RfqMatchId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Supplier>()
            .WithMany()
            .HasForeignKey(x => x.SupplierId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>Append-only revision rows (domain-model §6.2): unique per quote, chained to their predecessor.</summary>
public sealed class QuoteRevisionConfiguration : IEntityTypeConfiguration<QuoteRevision>
{
    public void Configure(EntityTypeBuilder<QuoteRevision> builder)
    {
        builder.ToTable("QuoteRevisions", t =>
        {
            t.HasCheckConstraint("CK_QuoteRevisions_Status", EnumIn<QuoteRevisionStatus>("Status"));
            t.HasCheckConstraint("CK_QuoteRevisions_Kind", EnumIn<QuoteRevisionKind>("RevisionKind"));
            t.HasCheckConstraint("CK_QuoteRevisions_TaxState", EnumIn<TaxState>("TaxState"));
        });

        builder.HasKey(x => x.PublicId);
        builder.Property(x => x.PublicId).ValueGeneratedNever();
        builder.Property(x => x.RevisionKind).HasEnumString();
        builder.Property(x => x.TaxState).HasEnumString();
        builder.Property(x => x.Status).HasEnumString();
        builder.Property(x => x.CurrencyCode).HasMaxLength(3).IsRequired();
        builder.Property(x => x.Subtotal).HasPrecision(18, 4);
        builder.Property(x => x.DiscountTotal).HasPrecision(18, 4);
        builder.Property(x => x.DeliveryCost).HasPrecision(18, 4);
        builder.Property(x => x.TaxAmount).HasPrecision(18, 4);
        builder.Property(x => x.GrandTotal).HasPrecision(18, 4);
        builder.Property(x => x.PaymentTerms).HasMaxLength(300);
        builder.Property(x => x.DeliveryTerms).HasMaxLength(300);
        builder.Property(x => x.Inclusions).HasMaxLength(600);
        builder.Property(x => x.Exclusions).HasMaxLength(600);
        builder.Property(x => x.PackSizeNote).HasMaxLength(300);
        builder.Property(x => x.MoqNote).HasMaxLength(300);
        builder.Property(x => x.TechnicalNotes).HasMaxLength(2000);
        builder.Property(x => x.SupplierNote).HasMaxLength(1000);

        builder.HasIndex(x => new { x.QuoteId, x.RevisionNumber }).IsUnique(); // §6.2

        builder.HasOne<Quote>()
            .WithMany()
            .HasForeignKey(x => x.QuoteId)
            .OnDelete(DeleteBehavior.Cascade);

        // Self-reference to the revision this one supersedes: Restrict, because
        // history is never rewritten and a predecessor may never disappear.
        builder.HasOne<QuoteRevision>()
            .WithMany()
            .HasForeignKey(x => x.PreviousRevisionId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>
/// Quoted lines (domain-model §6.2 <c>QuoteRevisionItem</c>). Every reference out
/// of this table is <c>Restrict</c>: the product, the unit and the RFQ line a
/// price answers are all snapshotted into the row, so nothing here may be
/// silently rewritten — or removed — by a delete elsewhere. (The line pointer
/// cannot be <c>SetNull</c> even if it were wanted: the RFQ → quote → revision
/// path already cascades into this table.)
/// </summary>
public sealed class QuoteRevisionItemConfiguration : IEntityTypeConfiguration<QuoteRevisionItem>
{
    public void Configure(EntityTypeBuilder<QuoteRevisionItem> builder)
    {
        builder.ToTable("QuoteRevisionItems");

        builder.HasKey(x => x.PublicId);
        builder.Property(x => x.PublicId).ValueGeneratedNever();
        builder.Property(x => x.ProductNameSnapshot).HasMaxLength(500).IsRequired(); // widest source: a product name (300) or the RFQ line description (500)
        builder.Property(x => x.SkuSnapshot).HasMaxLength(100);
        builder.Property(x => x.Description).HasMaxLength(500).IsRequired();
        builder.Property(x => x.Quantity).HasPrecision(18, 4);
        builder.Property(x => x.UnitPrice).HasPrecision(18, 4);
        builder.Property(x => x.LineDiscountPct).HasPrecision(9, 4);
        builder.Property(x => x.LineTotal).HasPrecision(18, 4);
        builder.Property(x => x.SubstitutionNote).HasMaxLength(500);

        builder.HasIndex(x => x.RevisionId);

        builder.HasOne<QuoteRevision>()
            .WithMany()
            .HasForeignKey(x => x.RevisionId)
            .OnDelete(DeleteBehavior.Cascade);

        // Restrict, not SetNull: deleting an RFQ already reaches these rows through
        // Quotes → QuoteRevisions → QuoteRevisionItems, so a second cascading path
        // back to RFQItems is rejected by SQL Server — and blocking the delete of a
        // line a quote answers is the immutability §3.10 actually wants.
        builder.HasOne<RfqItem>()
            .WithMany()
            .HasForeignKey(x => x.RfqItemId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Product>()
            .WithMany()
            .HasForeignKey(x => x.ProductId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<UnitOfMeasure>()
            .WithMany()
            .HasForeignKey(x => x.UnitOfMeasureId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
