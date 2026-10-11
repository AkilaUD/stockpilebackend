using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Stockpile.Domain.Common;
using Stockpile.Domain.Entities.Identity;

namespace Stockpile.Infrastructure.Persistence.Configurations;

public sealed class PasswordResetTokenConfiguration : IEntityTypeConfiguration<PasswordResetToken>
{
    public void Configure(EntityTypeBuilder<PasswordResetToken> builder)
    {
        builder.ToTable("PasswordResetTokens");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever(); // Guid v4, code-assigned

        builder.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
        builder.Ignore(x => x.Events); // outbox transport, never persisted (architecture §10.1)

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        // The completion flow looks up by (user, hash); the expiry metadata and
        // the append-only single-use ledger are read off the row itself.
        builder.HasIndex(x => new { x.UserId, x.TokenHash });
    }
}