using Stockpile.Domain.Enums;

namespace Stockpile.Domain.Entities.Identity;

// CA1711: "Permission"/"RolePermission" are the spec-mandated entity names
// (domain-model §2.2). The rule guards a retired CAS concept that does not
// apply to this domain.
#pragma warning disable CA1711

/// <summary>
/// Role (domain-model §2.2) — natural key: the code is the identity, seeded from
/// configuration (<c>Seed:Roles</c>), never hard-coded in logic. The spec's
/// <c>RoleId</c> references are this code.
/// </summary>
public sealed class Role
{
    /// <summary>e.g. <c>platformAdministrator</c>, <c>buyerAdmin</c>, <c>supplierOwner</c>.</summary>
    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public RoleScope Scope { get; set; }
}

/// <summary>
/// Permission (domain-model §2.2) — natural key mirroring the
/// <c>Contracts/Permissions</c> constants. A permission grants capability only,
/// never object access (architecture §8.2).
/// </summary>
public sealed class Permission
{
    /// <summary>e.g. <c>rfq.create</c>, <c>quote.accept</c>.</summary>
    public string Code { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    /// <summary>Group prefix, e.g. <c>rfq</c>, <c>quote</c>, <c>security</c>.</summary>
    public string Category { get; set; } = string.Empty;
}

/// <summary>Role ↔ permission grant (domain-model §2.2).</summary>
public sealed class RolePermission
{
    public string RoleCode { get; set; } = string.Empty;

    public string PermissionCode { get; set; } = string.Empty;
}

/// <summary>User ↔ role assignment (domain-model §2.1 children).</summary>
public sealed class UserRole
{
    public Guid UserId { get; set; }

    public string RoleCode { get; set; } = string.Empty;
}

#pragma warning restore CA1711
