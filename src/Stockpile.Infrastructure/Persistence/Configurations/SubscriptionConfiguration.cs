using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Stockpile.Domain.Entities.Catalogue;
using Stockpile.Domain.Entities.Subscriptions;
using Stockpile.Domain.Enums;
using static Stockpile.Infrastructure.Persistence.ModelBuilderExtensions;

namespace Stockpile.Infrastructure.Persistence.Configurations;

/// <summary>Subscription plan configuration rows (domain-model §3.5). Code is the unique business key.</summary>
public sealed class SubscriptionPlanConfiguration : IEntityTypeConfiguration<SubscriptionPlan>
{
    public void Configure(EntityTypeBuilder<SubscriptionPlan> builder)
    {
        builder.ToTable("SubscriptionPlans", t =>
        {
            t.HasCheckConstraint("CK_SubscriptionPlans_BillingPeriod", EnumIn<BillingPeriod>("BillingPeriod"));
            t.HasCheckConstraint("CK_SubscriptionPlans_Price", "[PriceAmount] IS NULL OR [PriceAmount] >= 0");
        });

        builder.HasKey(x => x.PublicId);
        builder.Property(x => x.PublicId).ValueGeneratedNever(); // Guid v7, code-assigned
        builder.Property(x => x.Code).HasMaxLength(40).IsRequired();
        builder.Property(x => x.Name).HasMaxLength(100).IsRequired();
        builder.Property(x => x.PriceAmount).HasPrecision(18, 4);
        builder.Property(x => x.CurrencyCode).HasMaxLength(3).IsRequired();
        builder.Property(x => x.BillingPeriod).HasEnumString();
        builder.Property(x => x.RowVersion).IsRowVersion();

        builder.HasIndex(x => x.Code).IsUnique();
        builder.HasIndex(x => new { x.IsActive, x.SortOrder });

        builder.HasMany(x => x.Entitlements)
            .WithOne()
            .HasForeignKey(x => x.PlanId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>Plan entitlements: composite key (plan, key) so a plan can state each entitlement once.</summary>
public sealed class SubscriptionEntitlementConfiguration : IEntityTypeConfiguration<SubscriptionEntitlement>
{
    public void Configure(EntityTypeBuilder<SubscriptionEntitlement> builder)
    {
        builder.ToTable("SubscriptionEntitlements", t =>
        {
            t.HasCheckConstraint(
                "CK_SubscriptionEntitlements_Limit",
                "[LimitValue] IS NULL OR ([LimitValue] >= 0 AND [IsUnlimited] = 0)");
        });

        builder.HasKey(x => new { x.PlanId, x.EntitlementKey });
        builder.Property(x => x.EntitlementKey).HasMaxLength(40).IsRequired();
        builder.Property(x => x.TextValue).HasMaxLength(100);
    }
}

/// <summary>
/// Supplier subscription. The filtered unique index keeps one non-terminal row per
/// supplier; <c>Cancelled</c> and <c>Expired</c> are the terminal states a supplier
/// can start over from. Plan and supplier references are <c>Restrict</c> — billing
/// history outlives a plan edit.
/// </summary>
public sealed class SupplierSubscriptionConfiguration : IEntityTypeConfiguration<SupplierSubscription>
{
    public void Configure(EntityTypeBuilder<SupplierSubscription> builder)
    {
        builder.ToTable("SupplierSubscriptions", t =>
        {
            t.HasCheckConstraint("CK_SupplierSubscriptions_Status", EnumIn<SubscriptionStatus>("Status"));
        });

        builder.HasKey(x => x.PublicId);
        builder.Property(x => x.PublicId).ValueGeneratedNever(); // Guid v4, code-assigned
        builder.Property(x => x.Status).HasEnumString();
        builder.Property(x => x.CancelReason).HasMaxLength(400);
        builder.Property(x => x.RowVersion).IsRowVersion();

        builder.HasIndex(x => x.SupplierId)
            .IsUnique()
            .HasFilter("[Status] <> 'Cancelled' AND [Status] <> 'Expired'")
            .HasDatabaseName("UX_SupplierSubscriptions_Supplier_NonTerminal");
        builder.HasIndex(x => x.PlanId);

        builder.HasOne<Supplier>().WithMany().HasForeignKey(x => x.SupplierId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<SubscriptionPlan>().WithMany().HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>Invoices: financial records, never cascaded away with their subscription.</summary>
public sealed class SubscriptionInvoiceConfiguration : IEntityTypeConfiguration<SubscriptionInvoice>
{
    public void Configure(EntityTypeBuilder<SubscriptionInvoice> builder)
    {
        builder.ToTable("SubscriptionInvoices", t =>
        {
            t.HasCheckConstraint("CK_SubscriptionInvoices_Status", EnumIn<InvoiceStatus>("Status"));
            t.HasCheckConstraint("CK_SubscriptionInvoices_Amounts", "[Amount] >= 0 AND [TaxAmount] >= 0 AND [Total] >= 0");
        });

        builder.HasKey(x => x.PublicId);
        builder.Property(x => x.PublicId).ValueGeneratedNever();
        builder.Property(x => x.InvoiceNumber).HasMaxLength(30).IsRequired();
        builder.Property(x => x.Amount).HasPrecision(18, 4);
        builder.Property(x => x.TaxAmount).HasPrecision(18, 4);
        builder.Property(x => x.Total).HasPrecision(18, 4);
        builder.Property(x => x.CurrencyCode).HasMaxLength(3).IsRequired();
        builder.Property(x => x.Status).HasEnumString();
        builder.Property(x => x.ExternalRef).HasMaxLength(100);
        builder.Property(x => x.ManualPaymentReason).HasMaxLength(400);
        builder.Property(x => x.RowVersion).IsRowVersion();

        builder.HasIndex(x => x.InvoiceNumber).IsUnique();
        builder.HasIndex(x => new { x.SubscriptionId, x.IssuedAt });
        builder.HasIndex(x => new { x.Status, x.IssuedAt });

        builder.HasOne<SupplierSubscription>()
            .WithMany()
            .HasForeignKey(x => x.SubscriptionId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>Payments; unique (Provider, ProviderRef) is the webhook idempotency backstop (§64).</summary>
public sealed class SubscriptionPaymentConfiguration : IEntityTypeConfiguration<SubscriptionPayment>
{
    public void Configure(EntityTypeBuilder<SubscriptionPayment> builder)
    {
        builder.ToTable("SubscriptionPayments", t =>
        {
            t.HasCheckConstraint("CK_SubscriptionPayments_Status", EnumIn<PaymentStatus>("Status"));
        });

        builder.HasKey(x => x.PublicId);
        builder.Property(x => x.PublicId).ValueGeneratedNever();
        builder.Property(x => x.Provider).HasMaxLength(40).IsRequired();
        builder.Property(x => x.ProviderRef).HasMaxLength(100).IsRequired();
        builder.Property(x => x.Amount).HasPrecision(18, 4);
        builder.Property(x => x.CurrencyCode).HasMaxLength(3).IsRequired();
        builder.Property(x => x.Status).HasEnumString();
        builder.Property(x => x.IdempotencyKey).HasMaxLength(64);
        builder.Property(x => x.RawPayload).HasMaxLength(4000);
        builder.Property(x => x.RowVersion).IsRowVersion();

        builder.HasIndex(x => new { x.Provider, x.ProviderRef }).IsUnique();
        builder.HasIndex(x => x.InvoiceId);

        builder.HasOne<SubscriptionInvoice>()
            .WithMany()
            .HasForeignKey(x => x.InvoiceId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
