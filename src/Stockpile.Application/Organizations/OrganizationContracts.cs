namespace Stockpile.Application.Organizations;

/// <summary>
/// Step 3 of the staged buyer registration (business-flows §2, api.md §2.1):
/// the organisation, its addresses, and optional team members. One atomic
/// create — an abandoned step never leaves a half-built organisation. Team
/// member entries require an existing account and invite by role (P9: only
/// organisation-scope roles assignable); rows land as Pending invitations, and
/// the caller joins immediately as the organisation administrator
/// (T6.4: invitations are the only other path to OrganizationMember).
/// </summary>
public sealed record CreateOrganizationRequest(
    OrganizationCompanyDraft Company,
    IReadOnlyList<AddressDraft>? Addresses,
    IReadOnlyList<OrganizationMemberDraft>? Members);

/// <summary>Company identity fields of the new organisation (domain-model §2.3 column lengths).</summary>
public sealed record OrganizationCompanyDraft(
    string Name,
    string? RegistrationNumber,
    string? TaxId,
    string? Website);

/// <summary>
/// A postal address draft (domain-model §2.4). <c>District</c> and
/// <c>Province</c> carry reference-data codes (e.g. <c>colombo</c>,
/// <c>western</c>) whose existence and pairing the use case confirms against
/// the seeded geography; <c>CountryCode</c> is ISO 3166-1 alpha-2.
/// </summary>
public sealed record AddressDraft(
    string Line1,
    string? Line2,
    string City,
    string District,
    string Province,
    string? PostalCode,
    string CountryCode,
    decimal? Latitude,
    decimal? Longitude,
    bool IsDefaultBilling,
    bool IsDefaultDelivery);

/// <summary>One team member to invite: an existing account (by email) and the organisation-scope role to assign.</summary>
public sealed record OrganizationMemberDraft(
    string Email,
    string RoleId);

/// <summary>
/// Add a member to an existing organisation (security-test-matrix P9): invite
/// an existing account by email with an organisation-scope role. Same shape and
/// rules as <see cref="OrganizationMemberDraft"/>; the caller must be an
/// accepted member with <c>organization.member.manage</c>.
/// </summary>
public sealed record CreateOrganizationMemberRequest(
    string Email,
    string RoleId);