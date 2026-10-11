using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Stockpile.Domain.Entities.Platform;
using Stockpile.Domain.Enums;
using static Stockpile.Infrastructure.Persistence.ModelBuilderExtensions;

namespace Stockpile.Infrastructure.Persistence.Configurations;

public sealed class AuditConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        builder.ToTable("AuditLogs", t =>
            t.HasCheckConstraint("CK_AuditLogs_Result", EnumIn<AuditResult>("Result")));

        // High-volume table → long identity surrogate (domain-model §1).
        // Actor columns are deliberately loose references (no FK): accountability
        // rows must outlive and never cascade from the actors they name.
        builder.Property(x => x.Result).HasEnumString();
        builder.Property(x => x.Action).HasMaxLength(100).IsRequired();
        builder.Property(x => x.TargetType).HasMaxLength(100).IsRequired();
        builder.Property(x => x.TargetId).HasMaxLength(100);
        builder.Property(x => x.IpAddress).HasMaxLength(45);
        builder.Property(x => x.UserAgent).HasMaxLength(400);
        builder.Property(x => x.CorrelationId).HasMaxLength(64);
        builder.Property(x => x.Metadata);

        builder.HasIndex(x => x.OccurredAt);
        builder.HasIndex(x => new { x.ActorId, x.OccurredAt });
        builder.HasIndex(x => new { x.Action, x.OccurredAt });
    }
}

public sealed class SecurityEventConfiguration : IEntityTypeConfiguration<SecurityEvent>
{
    public void Configure(EntityTypeBuilder<SecurityEvent> builder)
    {
        builder.ToTable("SecurityEvents", t =>
            t.HasCheckConstraint("CK_SecurityEvents_Type", EnumIn<SecurityEventType>("Type")));

        builder.Property(x => x.Type).HasEnumString();
        builder.Property(x => x.IpAddress).HasMaxLength(45);
        builder.Property(x => x.UserAgent).HasMaxLength(400);
        builder.Property(x => x.CorrelationId).HasMaxLength(64);
        builder.Property(x => x.Detail).HasMaxLength(500);
        builder.Property(x => x.Metadata);

        // Separate retention (2 years) from AuditLog (7 years) — §14.
        builder.HasIndex(x => x.OccurredAt);
        builder.HasIndex(x => new { x.UserId, x.OccurredAt });
    }
}
