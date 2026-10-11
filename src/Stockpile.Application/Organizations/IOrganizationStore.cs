using Stockpile.Domain.Entities.Identity;
using Stockpile.Domain.Entities.Reference;
using Stockpile.Domain.Enums;

namespace Stockpile.Application.Organizations;

/// <summary>Task 1.43 — the organisation data port (architecture §8.5 modules: organizations, members, addresses). Operations stay transaction-shaped on the store side; Application owns the rules.</summary>
public interface IOrganizationStore
{
    Task<Organization?> FindByIdAsync(Guid publicId, CancellationToken cancellationToken);

    /// <summary>The member row for (organization, user) at any invitation status, or null when none.</summary>
    Task<OrganizationMember?> FindMemberAsync(
        Guid organizationId, Guid userId, CancellationToken cancellationToken);

    /// <summary>Find an account by its Identity-normalized email (UpperInvariant).</summary>
    Task<User?> FindUserByNormalizedEmailAsync(string normalizedEmail, CancellationToken cancellationToken);

    Task<User?> FindUserByIdAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Roles with <see cref="RoleScope.Organization"/> — the only assignable member roles (P9).</summary>
    Task<IReadOnlyList<Role>> GetOrganizationScopedRolesAsync(CancellationToken cancellationToken);

    Task<Role?> FindRoleAsync(string code, CancellationToken cancellationToken);

    /// <summary>Create an organisation, its addresses and its member rows in one SaveChanges (one transaction).</summary>
    Task CreateOrganizationAsync(
        Organization organization,
        IReadOnlyCollection<Address> addresses,
        IReadOnlyCollection<OrganizationMember> members,
        CancellationToken cancellationToken);

    /// <summary>Add one Pending invitation row (T6.4: the only non-creator path to OrganizationMember).</summary>
    Task AddMemberAsync(OrganizationMember member, CancellationToken cancellationToken);

    /// <summary>The user's own organisation memberships (every status) joined with the org's identity — the session payload source.</summary>
    Task<IReadOnlyList<OrganizationMembership>> GetMembershipsAsync(Guid userId, CancellationToken cancellationToken);

    // ---- Reference geography (domain-model §2.4) ----

    Task<District?> FindDistrictAsync(string code, CancellationToken cancellationToken);

    Task<Province?> FindProvinceAsync(string code, CancellationToken cancellationToken);

    Task<Country?> FindCountryAsync(string code, CancellationToken cancellationToken);

    // ---- User-owned addresses (/me/addresses, api.md §2.2) ----

    Task<Address?> FindUserAddressAsync(Guid addressId, Guid userId, CancellationToken cancellationToken);

    Task<IReadOnlyList<Address>> GetUserAddressesAsync(Guid userId, CancellationToken cancellationToken);

    Task AddAddressAsync(Address address, CancellationToken cancellationToken);

    Task UpdateAddressAsync(Address address, CancellationToken cancellationToken);

    /// <summary>Hard delete: an address is ordinary data, not an append-only fact. Only user-owned addresses reach here.</summary>
    Task DeleteAddressAsync(Guid addressId, CancellationToken cancellationToken);
}

/// <summary>One membership fact joined with its organisation for the session payload.</summary>
public sealed record OrganizationMembership(
    Guid OrganizationId,
    string OrganizationName,
    string RoleCode,
    InvitationStatus Status);