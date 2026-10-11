using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Stockpile.Domain.Entities.Identity;

namespace Stockpile.Infrastructure.Persistence.Configurations;

public sealed class LoginSessionConfiguration : IEntityTypeConfiguration<LoginSession>
{
    public void Configure(EntityTypeBuilder<LoginSession> builder)
    {
        builder.ToTable("LoginSessions");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever(); // Guid v4, code-assigned

        builder.Property(x => x.IpAddress).HasMaxLength(45);
        builder.Property(x => x.UserAgent).HasMaxLength(400);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        // Session listing per user + expiry sweep.
        builder.HasIndex(x => new { x.UserId, x.ExpiresAt });
        builder.HasIndex(x => x.ExpiresAt);
    }
}

public sealed class RefreshSessionConfiguration : IEntityTypeConfiguration<RefreshSession>
{
    public void Configure(EntityTypeBuilder<RefreshSession> builder)
    {
        builder.ToTable("RefreshSessions");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever(); // Guid v4, code-assigned

        builder.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();

        builder.HasOne<LoginSession>()
            .WithMany()
            .HasForeignKey(x => x.SessionId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<RefreshSession>()
            .WithMany()
            .HasForeignKey(x => x.RotatedToId)
            .OnDelete(DeleteBehavior.Restrict);

        // Only the hash is stored; lookups are by hash (§2.5).
        builder.HasIndex(x => x.TokenHash).IsUnique().HasDatabaseName("UX_RefreshSessions_TokenHash");
        builder.HasIndex(x => x.SessionId);
    }
}
