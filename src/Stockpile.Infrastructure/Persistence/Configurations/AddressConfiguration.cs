using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Stockpile.Domain.Entities.Identity;
using Stockpile.Domain.Entities.Reference;

namespace Stockpile.Infrastructure.Persistence.Configurations;

public sealed class AddressConfiguration : IEntityTypeConfiguration<Address>
{
    public void Configure(EntityTypeBuilder<Address> builder)
    {
        builder.ToTable("Addresses", t =>
        {
            // Exactly one owner (domain-model §12 ownership rule).
            t.HasCheckConstraint(
                "CK_Addresses_Owner",
                "([OrganizationId] IS NOT NULL AND [UserId] IS NULL) OR ([OrganizationId] IS NULL AND [UserId] IS NOT NULL)");
            t.HasCheckConstraint(
                "CK_Addresses_Latitude",
                "[Latitude] IS NULL OR ([Latitude] >= -90 AND [Latitude] <= 90)");
            t.HasCheckConstraint(
                "CK_Addresses_Longitude",
                "[Longitude] IS NULL OR ([Longitude] >= -180 AND [Longitude] <= 180)");
        });

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever(); // Guid, code-assigned

        builder.Property(x => x.Line1).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Line2).HasMaxLength(200);
        builder.Property(x => x.City).HasMaxLength(100).IsRequired();
        builder.Property(x => x.District).HasMaxLength(20).IsRequired();
        builder.Property(x => x.Province).HasMaxLength(20).IsRequired();
        builder.Property(x => x.PostalCode).HasMaxLength(20);
        builder.Property(x => x.CountryCode).HasMaxLength(2).IsRequired();
        builder.Property(x => x.Latitude).HasPrecision(9, 6);
        builder.Property(x => x.Longitude).HasPrecision(9, 6);

        // Reference codes, not free text — filtering joins on code (§2.4).
        builder.HasOne<Country>()
            .WithMany()
            .HasForeignKey(x => x.CountryCode)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<District>()
            .WithMany()
            .HasForeignKey(x => x.District)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Province>()
            .WithMany()
            .HasForeignKey(x => x.Province)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(x => x.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => x.OrganizationId);
        builder.HasIndex(x => x.UserId);
    }
}
