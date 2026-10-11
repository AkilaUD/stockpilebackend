using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Stockpile.Domain.Entities.Identity;
using Stockpile.Domain.Enums;
using static Stockpile.Infrastructure.Persistence.ModelBuilderExtensions;

namespace Stockpile.Infrastructure.Persistence.Configurations;

public sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("Users", t =>
        {
            t.HasCheckConstraint("CK_Users_Status", EnumIn<UserStatus>("Status"));
            t.HasCheckConstraint("CK_Users_FailedAccessCount", "[FailedAccessCount] >= 0");
            t.HasCheckConstraint("CK_Users_LockoutEpisode", "[LockoutEpisode] >= 0");
        });

        // Public id: assigned in code (Guid v4), never a DB default (domain-model §1).
        builder.HasKey(x => x.PublicId);
        builder.Property(x => x.PublicId).ValueGeneratedNever();

        builder.Property(x => x.Email).HasMaxLength(256).IsRequired();
        builder.Property(x => x.EmailNormalized).HasMaxLength(256).IsRequired();
        builder.Property(x => x.PasswordHash).IsRequired(); // nvarchar(max): body-class field
        builder.Property(x => x.FirstName).HasMaxLength(100).IsRequired();
        builder.Property(x => x.LastName).HasMaxLength(100).IsRequired();
        builder.Property(x => x.Phone).HasMaxLength(30);
        builder.Property(x => x.PreferredLocale).HasMaxLength(10).IsRequired();
        builder.Property(x => x.TwoFactorSecret).HasMaxLength(64); // base32 RFC 6238 secret (task 1.44)
        builder.Property(x => x.Status).HasEnumString();
        builder.Property(x => x.RowVersion).IsRowVersion();

        // Case-insensitive login key: the normalized email is globally unique (§2.1).
        builder.HasIndex(x => x.EmailNormalized)
            .IsUnique()
            .HasDatabaseName("UX_Users_EmailNormalized");
    }
}
