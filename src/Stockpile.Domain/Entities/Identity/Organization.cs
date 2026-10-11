using Stockpile.Domain.Common;
using Stockpile.Domain.Enums;

namespace Stockpile.Domain.Entities.Identity;

/// <summary>
/// Aggregate root (domain-model §2.3). Organisations — not users — own RFQs and
/// suppliers, which is what makes team procurement work. Guid v7 public id.
/// </summary>
public sealed class Organization : AuditableEntity
{
    public Guid PublicId { get; set; } = Guid.CreateVersion7();

    public string Name { get; set; } = string.Empty;

    public OrganizationType Type { get; set; }

    public string? RegistrationNumber { get; set; }

    public string? TaxId { get; set; }

    public string? Website { get; set; }

    public OrganizationStatus Status { get; set; } = OrganizationStatus.Active;

    /// <summary>Preferred address pointer; SetNull on address deletion (architecture §9.1).</summary>
    public Guid? PrimaryAddressId { get; set; }

    /// <summary>SQL rowversion for optimistic concurrency on this aggregate root.</summary>
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>
/// Membership of a user in an organisation (domain-model §2.3). Authorization
/// resolves through here → Organization → aggregate owner; there is no
/// "user owns RFQ" rule. A member may hold several roles in the same
/// organisation (one row each) — that is how staff of a <c>Both</c>-type
/// company receive buyer and supplier capabilities together.
/// </summary>
public sealed class OrganizationMember
{
    public Guid OrganizationId { get; set; }

    public Guid UserId { get; set; }

    /// <summary>Role code (spec field name <c>RoleId</c>); scoped <see cref="RoleScope.Organization"/> or <see cref="RoleScope.Supplier"/>.</summary>
    public string RoleCode { get; set; } = string.Empty;

    public InvitationStatus InvitationStatus { get; set; } = InvitationStatus.Pending;

    /// <summary>Set when the invitation is accepted; null while pending.</summary>
    public DateTime? JoinedAt { get; set; }
}
