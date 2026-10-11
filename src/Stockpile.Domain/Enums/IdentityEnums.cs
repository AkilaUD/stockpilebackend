namespace Stockpile.Domain.Enums;

/// <summary>User account state (domain-model §2.1). Soft delete: Deleted keeps the auth trail.</summary>
public enum UserStatus
{
    Active = 0,
    Suspended = 1,
    Deleted = 2,
}

/// <summary>Organisation kind (domain-model §2.3).</summary>
public enum OrganizationType
{
    BuyerCompany = 0,
    SupplierCompany = 1,
    Both = 2,
    Individual = 3,
}

/// <summary>Organisation state (domain-model §2.3).</summary>
public enum OrganizationStatus
{
    Active = 0,
    Suspended = 1,
    Closed = 2,
}

/// <summary>Membership invitation lifecycle (domain-model §2.3).</summary>
public enum InvitationStatus
{
    Pending = 0,
    Accepted = 1,
    Declined = 2,
    Revoked = 3,
}

/// <summary>
/// Role scope (domain-model §2.2). Platform roles are internal operators,
/// organisation roles manage a buyer/supplier company, supplier roles manage a
/// supplier's own records.
/// </summary>
public enum RoleScope
{
    Platform = 0,
    Organization = 1,
    Supplier = 2,
}
