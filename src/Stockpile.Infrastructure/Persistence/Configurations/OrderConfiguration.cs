using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Stockpile.Domain.Entities.Catalogue;
using Stockpile.Domain.Entities.Identity;
using Stockpile.Domain.Entities.Orders;
using Stockpile.Domain.Entities.Procurement;
using Stockpile.Domain.Enums;
using static Stockpile.Infrastructure.Persistence.ModelBuilderExtensions;

namespace Stockpile.Infrastructure.Persistence.Configurations;

/// <summary>
/// Negotiation thread rows (domain-model §6.3): append-only, cascade from the
/// quote they discuss, enum columns nvarchar(40) + CHECK.
/// </summary>
public sealed class NegotiationMessageConfiguration : IEntityTypeConfiguration<NegotiationMessage>
{
    public void Configure(EntityTypeBuilder<NegotiationMessage> builder)
    {
        builder.ToTable("NegotiationMessages", t =>
        {
            t.HasCheckConstraint("CK_NegotiationMessages_Direction", EnumIn<NegotiationDirection>("Direction"));
            t.HasCheckConstraint("CK_NegotiationMessages_Kind", EnumIn<NegotiationKind>("Kind"));
        });

        builder.HasKey(x => x.PublicId);
        builder.Property(x => x.PublicId).ValueGeneratedNever(); // Guid v4, code-assigned
        builder.Property(x => x.Direction).HasEnumString();
        builder.Property(x => x.Kind).HasEnumString();
        builder.Property(x => x.Body).HasMaxLength(1000).IsRequired();

        builder.HasIndex(x => new { x.QuoteId, x.CreatedAt });

        builder.HasOne<Quote>()
            .WithMany()
            .HasForeignKey(x => x.QuoteId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>
/// Purchase order aggregate (domain-model §7.1). One order per quote (unique
/// <c>QuoteId</c>) is the backstop behind the idempotent, race-safe accept. Every
/// cross-aggregate reference is <c>Restrict</c>: an order is a commercial record
/// and must never vanish — or be rewritten — because something else was deleted.
/// </summary>
public sealed class PurchaseOrderConfiguration : IEntityTypeConfiguration<PurchaseOrder>
{
    public void Configure(EntityTypeBuilder<PurchaseOrder> builder)
    {
        builder.ToTable("PurchaseOrders", t =>
        {
            t.HasCheckConstraint("CK_PurchaseOrders_Status", EnumIn<PurchaseOrderStatus>("Status"));
            t.HasCheckConstraint("CK_PurchaseOrders_StockReservation", EnumIn<StockReservationState>("StockReservation"));
        });

        builder.HasKey(x => x.PublicId);
        builder.Property(x => x.PublicId).ValueGeneratedNever();
        builder.Property(x => x.OrderNumber).HasMaxLength(30).IsRequired();
        builder.Property(x => x.Status).HasEnumString();
        builder.Property(x => x.StockReservation).HasEnumString();
        builder.Property(x => x.CurrencyCode).HasMaxLength(3).IsRequired();
        builder.Property(x => x.Subtotal).HasPrecision(18, 4);
        builder.Property(x => x.DiscountTotal).HasPrecision(18, 4);
        builder.Property(x => x.DeliveryCost).HasPrecision(18, 4);
        builder.Property(x => x.TaxAmount).HasPrecision(18, 4);
        builder.Property(x => x.GrandTotal).HasPrecision(18, 4);
        builder.Property(x => x.PaymentTerms).HasMaxLength(300);
        builder.Property(x => x.DeliveryTerms).HasMaxLength(300);
        builder.Property(x => x.DeliveryLocationText).HasMaxLength(500);
        builder.Property(x => x.CancelReason).HasMaxLength(400);
        builder.Property(x => x.RowVersion).IsRowVersion();

        builder.HasIndex(x => x.OrderNumber).IsUnique();
        builder.HasIndex(x => x.QuoteId).IsUnique(); // one order per quote
        builder.HasIndex(x => new { x.BuyerOrganizationId, x.CreatedAt });
        builder.HasIndex(x => new { x.SupplierId, x.CreatedAt });

        builder.HasOne<Rfq>().WithMany().HasForeignKey(x => x.RfqId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Quote>().WithMany().HasForeignKey(x => x.QuoteId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<QuoteRevision>().WithMany().HasForeignKey(x => x.QuoteRevisionId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Supplier>().WithMany().HasForeignKey(x => x.SupplierId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Organization>().WithMany().HasForeignKey(x => x.BuyerOrganizationId).OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>Ordered lines: snapshots that cascade from their order only.</summary>
public sealed class PurchaseOrderItemConfiguration : IEntityTypeConfiguration<PurchaseOrderItem>
{
    public void Configure(EntityTypeBuilder<PurchaseOrderItem> builder)
    {
        builder.ToTable("PurchaseOrderItems", t =>
        {
            t.HasCheckConstraint("CK_PurchaseOrderItems_StockState", EnumIn<StockReservationState>("StockState"));
        });

        builder.HasKey(x => x.PublicId);
        builder.Property(x => x.PublicId).ValueGeneratedNever();
        builder.Property(x => x.StockState).HasEnumString();
        builder.Property(x => x.ProductNameSnapshot).HasMaxLength(500).IsRequired();
        builder.Property(x => x.SkuSnapshot).HasMaxLength(100);
        builder.Property(x => x.Description).HasMaxLength(500).IsRequired();
        builder.Property(x => x.Quantity).HasPrecision(18, 4);
        builder.Property(x => x.UnitPrice).HasPrecision(18, 4);
        builder.Property(x => x.LineDiscountPct).HasPrecision(9, 4);
        builder.Property(x => x.LineTotal).HasPrecision(18, 4);

        builder.HasIndex(x => new { x.PurchaseOrderId, x.LineNumber }).IsUnique();

        builder.HasOne<PurchaseOrder>()
            .WithMany()
            .HasForeignKey(x => x.PurchaseOrderId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<UnitOfMeasure>().WithMany().HasForeignKey(x => x.UnitOfMeasureId).OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>Append-only order status ledger (domain-model §7.3).</summary>
public sealed class PurchaseOrderStatusHistoryConfiguration : IEntityTypeConfiguration<PurchaseOrderStatusHistory>
{
    public void Configure(EntityTypeBuilder<PurchaseOrderStatusHistory> builder)
    {
        builder.ToTable("PurchaseOrderStatusHistories", t =>
        {
            t.HasCheckConstraint("CK_PurchaseOrderStatusHistories_From", EnumIn<PurchaseOrderStatus>("FromState"));
            t.HasCheckConstraint("CK_PurchaseOrderStatusHistories_To", EnumIn<PurchaseOrderStatus>("ToState"));
        });

        builder.HasKey(x => x.PublicId);
        builder.Property(x => x.PublicId).ValueGeneratedNever();
        builder.Property(x => x.FromState).HasEnumString();
        builder.Property(x => x.ToState).HasEnumString();
        builder.Property(x => x.Reason).HasMaxLength(400);
        builder.Property(x => x.CorrelationId).HasMaxLength(64);

        builder.HasIndex(x => new { x.PurchaseOrderId, x.OccurredAt });

        builder.HasOne<PurchaseOrder>()
            .WithMany()
            .HasForeignKey(x => x.PurchaseOrderId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
