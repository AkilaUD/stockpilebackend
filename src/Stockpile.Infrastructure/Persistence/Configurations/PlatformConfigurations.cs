using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Stockpile.Domain.Entities.Identity;
using Stockpile.Domain.Entities.Platform;
using Stockpile.Domain.Enums;
using static Stockpile.Infrastructure.Persistence.ModelBuilderExtensions;

namespace Stockpile.Infrastructure.Persistence.Configurations;

public sealed class IdempotencyKeyConfiguration : IEntityTypeConfiguration<IdempotencyKey>
{
    public void Configure(EntityTypeBuilder<IdempotencyKey> builder)
    {
        builder.ToTable("IdempotencyKeys");

        // Composite natural key (architecture §9.4): keys are scoped per owner.
        builder.HasKey(x => new { x.OwnerId, x.Key });

        builder.Property(x => x.Key).HasMaxLength(100);
        builder.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
        builder.Property(x => x.ResponseReference).HasMaxLength(400);

        // 7-day expiry sweep (§14 retention).
        builder.HasIndex(x => x.ExpiresAt);
    }
}

public sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("OutboxMessages", t =>
            t.HasCheckConstraint("CK_OutboxMessages_Status", EnumIn<OutboxMessageStatus>("Status")));

        builder.Property(x => x.EventType).HasMaxLength(100).IsRequired();
        builder.Property(x => x.Payload).IsRequired(); // nvarchar(max): payload field
        builder.Property(x => x.Status).HasEnumString();
        builder.Property(x => x.CorrelationId).HasMaxLength(64);
        builder.Property(x => x.DeadLetterReason).HasMaxLength(500);

        // The Worker claim: WHERE Status = 'Pending' AND NextAttemptAt <= now ORDER BY Id.
        builder.HasIndex(x => new { x.Status, x.NextAttemptAt });
    }
}

public sealed class StateTransitionConfiguration : IEntityTypeConfiguration<StateTransition>
{
    public void Configure(EntityTypeBuilder<StateTransition> builder)
    {
        // Seeded transition table (§11); wildcards like '*' live in FromState.
        // No enum CHECK here: rows span every machine, and each machine's own
        // state columns are check-constrained at the aggregate level.
        builder.HasKey(x => new { x.Entity, x.FromState, x.ToState });

        builder.Property(x => x.Entity).HasMaxLength(40);
        builder.Property(x => x.FromState).HasMaxLength(40);
        builder.Property(x => x.ToState).HasMaxLength(40);
        builder.Property(x => x.PermissionCode).HasMaxLength(100);
        builder.Property(x => x.Notes).HasMaxLength(300);

        builder.HasOne<Permission>()
            .WithMany()
            .HasForeignKey(x => x.PermissionCode)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class SystemSettingConfiguration : IEntityTypeConfiguration<SystemSetting>
{
    public void Configure(EntityTypeBuilder<SystemSetting> builder)
    {
        builder.ToTable("SystemSettings", t =>
            t.HasCheckConstraint("CK_SystemSettings_ValueType", EnumIn<SettingValueType>("ValueType")));

        builder.HasKey(x => x.Key);
        builder.Property(x => x.Key).HasMaxLength(100).ValueGeneratedNever();
        builder.Property(x => x.Value).IsRequired(); // nvarchar(max): payload field
        builder.Property(x => x.ValueType).HasEnumString();
    }
}

public sealed class FeatureFlagConfiguration : IEntityTypeConfiguration<FeatureFlag>
{
    public void Configure(EntityTypeBuilder<FeatureFlag> builder)
    {
        builder.ToTable("FeatureFlags", t =>
            t.HasCheckConstraint("CK_FeatureFlags_RolloutPercent", "[RolloutPercent] >= 0 AND [RolloutPercent] <= 100"));

        builder.HasKey(x => x.Code);
        builder.Property(x => x.Code).HasMaxLength(80).ValueGeneratedNever();
        builder.Property(x => x.AllowedEnvironments).HasMaxLength(200).IsRequired();
    }
}
