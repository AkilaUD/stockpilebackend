using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Stockpile.Domain.Common;
using Stockpile.Domain.Entities.Identity;

namespace Stockpile.Infrastructure.Persistence.Configurations;

public sealed class EmailVerificationTokenConfiguration : IEntityTypeConfiguration<EmailVerificationToken>
{
    public void Configure(EntityTypeBuilder<EmailVerificationToken> builder)
    {
        builder.ToTable("EmailVerificationTokens");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever(); // Guid v4, code-assigned

        builder.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
        builder.Ignore(x => x.Events); // outbox transport, never persisted (architecture §10.1)

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        // The anonymous verify endpoint looks up by hash ALONE (the token
        // identifies its account), so the hash carries a unique index for a
        // single-key probe query.
        builder.HasIndex(x => x.TokenHash).IsUnique();
    }
}