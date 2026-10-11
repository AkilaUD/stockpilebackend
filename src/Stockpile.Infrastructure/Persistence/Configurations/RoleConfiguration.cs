using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Stockpile.Domain.Entities.Identity;
using Stockpile.Domain.Enums;
using static Stockpile.Infrastructure.Persistence.ModelBuilderExtensions;

namespace Stockpile.Infrastructure.Persistence.Configurations;

public sealed class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> builder)
    {
        builder.ToTable("Roles", t => t.HasCheckConstraint("CK_Roles_Scope", EnumIn<RoleScope>("Scope")));

        // Natural key: the role code is the identity (domain-model §2.2).
        builder.HasKey(x => x.Code);
        builder.Property(x => x.Code).HasMaxLength(80).ValueGeneratedNever();
        builder.Property(x => x.Name).HasMaxLength(100).IsRequired();
        builder.Property(x => x.Scope).HasEnumString();
    }
}

public sealed class PermissionConfiguration : IEntityTypeConfiguration<Permission>
{
    public void Configure(EntityTypeBuilder<Permission> builder)
    {
        builder.ToTable("Permissions");

        // Natural key mirroring Contracts/Permissions constants (domain-model §2.2).
        builder.HasKey(x => x.Code);
        builder.Property(x => x.Code).HasMaxLength(100).ValueGeneratedNever();
        builder.Property(x => x.Description).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Category).HasMaxLength(40).IsRequired();
    }
}

public sealed class RolePermissionConfiguration : IEntityTypeConfiguration<RolePermission>
{
    public void Configure(EntityTypeBuilder<RolePermission> builder)
    {
        builder.ToTable("RolePermissions");

        builder.HasKey(x => new { x.RoleCode, x.PermissionCode });
        builder.Property(x => x.RoleCode).HasMaxLength(80);
        builder.Property(x => x.PermissionCode).HasMaxLength(100);

        // Roles and permissions are configuration-seeded and never deleted (§13).
        builder.HasOne<Role>()
            .WithMany()
            .HasForeignKey(x => x.RoleCode)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Permission>()
            .WithMany()
            .HasForeignKey(x => x.PermissionCode)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class UserRoleConfiguration : IEntityTypeConfiguration<UserRole>
{
    public void Configure(EntityTypeBuilder<UserRole> builder)
    {
        builder.ToTable("UserRoles");

        builder.HasKey(x => new { x.UserId, x.RoleCode });
        builder.Property(x => x.RoleCode).HasMaxLength(80);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Role>()
            .WithMany()
            .HasForeignKey(x => x.RoleCode)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
