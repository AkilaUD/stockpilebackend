using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Stockpile.Domain.Entities.Identity;
using Stockpile.Domain.Enums;
using static Stockpile.Infrastructure.Persistence.ModelBuilderExtensions;

namespace Stockpile.Infrastructure.Persistence.Configurations;

public sealed class PreferencesConfiguration : IEntityTypeConfiguration<UserPreference>
{
    public void Configure(EntityTypeBuilder<UserPreference> builder)
    {
        builder.ToTable("UserPreferences");

        builder.HasKey(x => new { x.UserId, x.Key });
        builder.Property(x => x.Key).HasMaxLength(80);
        builder.Property(x => x.Value).HasMaxLength(400).IsRequired();

        // Hard delete with the account (§13); the user row itself is never removed.
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class NotificationPreferenceConfiguration : IEntityTypeConfiguration<NotificationPreference>
{
    public void Configure(EntityTypeBuilder<NotificationPreference> builder)
    {
        builder.ToTable("NotificationPreferences", t =>
            t.HasCheckConstraint("CK_NotificationPreferences_Channel", EnumIn<NotificationChannel>("Channel")));

        builder.HasKey(x => new { x.UserId, x.Category, x.Channel });
        builder.Property(x => x.Category).HasMaxLength(60);
        builder.Property(x => x.Channel).HasEnumString();
        builder.Property(x => x.Enabled).HasDefaultValue(true);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
