namespace Stockpile.Contracts.Organizations;

/// <summary>
/// An organisation on the wire (api.md organisation endpoints, domain-model
/// §2.3). <c>Type</c>/<c>Status</c> are the enum wire values; members are the
/// rows this organisation currently has — the creator joins immediately at
/// creation, invited team members ride as <c>Pending</c> until the invite
/// accept flow (threat-model T6.4). Serialized camelCase; optional members are
/// omitted when absent, never null (api.md §1.4).
/// </summary>
public sealed record OrganizationResponse(
    string Id,
    string Name,
    string Type,
    string Status,
    string? RegistrationNumber,
    string? TaxId,
    string? Website,
    IReadOnlyList<AddressResponse> Addresses,
    IReadOnlyList<OrganizationMemberResponse> Members);

/// <summary>One postal address (domain-model §2.4). <c>District</c>/<c>Province</c> are reference codes, never free text.</summary>
public sealed record AddressResponse(
    string Id,
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

/// <summary>One <c>OrganizationMember</c> row (domain-model §2.3): who, which role, and the invitation lifecycle state.</summary>
public sealed record OrganizationMemberResponse(
    string UserId,
    string Email,
    string Role,
    string Status,
    string? JoinedAt);