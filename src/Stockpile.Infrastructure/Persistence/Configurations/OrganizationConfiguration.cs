using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Stockpile.Domain.Entities.Identity;
using Stockpile.Domain.Enums;
using static Stockpile.Infrastructure.Persistence.ModelBuilderExtensions;

namespace Stockpile.Infrastructure.Persistence.Configurations;

public sealed class OrganizationConfiguration : IEntityTypeConfiguration<Organization>
{
    public void Configure(EntityTypeBuilder<Organization> builder)
    {
        builder.ToTable("Organizations", t =>
            t.HasCheckConstraint("CK_Organizations_Status", EnumIn<OrganizationStatus>("Status")));

        builder.HasKey(x => x.PublicId);
        builder.Property(x => x.PublicId).ValueGeneratedNever(); // Guid v7, code-assigned

        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Type).HasEnumString();
        builder.Property(x => x.RegistrationNumber).HasMaxLength(60);
        builder.Property(x => x.TaxId).HasMaxLength(60);
        builder.Property(x => x.Website).HasMaxLength(300);
        builder.Property(x => x.Status).HasEnumString();
        builder.Property(x => x.RowVersion).IsRowVersion();

        // Optional pointer: dropping the address clears the pointer (architecture §9.1).
        builder.HasOne<Address>()
            .WithMany()
            .HasForeignKey(x => x.PrimaryAddressId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

public sealed class OrganizationMemberConfiguration : IEntityTypeConfiguration<OrganizationMember>
{
    public void Configure(EntityTypeBuilder<OrganizationMember> builder)
    {
        builder.ToTable("OrganizationMembers", t =>
            t.HasCheckConstraint("CK_OrganizationMembers_InvitationStatus",
                EnumIn<InvitationStatus>("InvitationStatus")));

        // One row per (organisation, user, role): multi-role membership is what
        // lets Both-type organisation staff hold buyer and supplier roles.
        builder.HasKey(x => new { x.OrganizationId, x.UserId, x.RoleCode });

        builder.Property(x => x.RoleCode).HasMaxLength(80).IsRequired();

        // Enum → member name (domain-model §1; architecture §9.1): the CHECK
        // constraint compares against names, so the column must be a string.
        builder.Property(x => x.InvitationStatus).HasEnumString();

        builder.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(x => x.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Role>()
            .WithMany()
            .HasForeignKey(x => x.RoleCode)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => x.RoleCode);
    }
}
