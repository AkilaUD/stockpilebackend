using Microsoft.EntityFrameworkCore;
using Stockpile.Application.Organizations;
using Stockpile.Domain.Entities.Identity;
using Stockpile.Domain.Entities.Reference;
using Stockpile.Domain.Enums;

namespace Stockpile.Infrastructure.Persistence;

/// <summary>
/// Task 1.43 — <see cref="IOrganizationStore"/> over the schema's
/// <c>Organizations</c>, <c>OrganizationMembers</c> and <c>Addresses</c> tables
/// (domain-model §2.3/§2.4). Creation is ONE transaction so the organisation,
/// its addresses and its member rows commit atomically — an abandoned wizard
/// step 3 must never leave a half-built organisation (business-flows §2);
/// storage ordering (see <see cref="CreateOrganizationAsync"/>) keeps the
/// primary-address FK cycle satisfiable under the owner CHECK constraint.
/// Membership rows are append-only facts (X9), the same discipline as the
/// session stores: this store inserts invitations but never deletes or
/// revokes them — acceptance, role change and removal are later phases that
/// must stamp state, never DELETEs.
/// </summary>
public sealed class OrganizationStore(StockpileDbContext db) : IOrganizationStore
{
    /// <inheritdoc />
    public Task<Organization?> FindByIdAsync(Guid publicId, CancellationToken cancellationToken)
        => db.Organizations.FirstOrDefaultAsync(organization => organization.PublicId == publicId, cancellationToken);

    /// <inheritdoc />
    public Task<OrganizationMember?> FindMemberAsync(
        Guid organizationId, Guid userId, CancellationToken cancellationToken)
        => db.OrganizationMembers.FirstOrDefaultAsync(
            member => member.OrganizationId == organizationId && member.UserId == userId, cancellationToken);

    /// <inheritdoc />
    public Task<User?> FindUserByNormalizedEmailAsync(string normalizedEmail, CancellationToken cancellationToken)
        => db.Users.FirstOrDefaultAsync(user => user.EmailNormalized == normalizedEmail, cancellationToken);

    /// <inheritdoc />
    public Task<User?> FindUserByIdAsync(Guid userId, CancellationToken cancellationToken)
        => db.Users.FirstOrDefaultAsync(user => user.PublicId == userId, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<Role>> GetOrganizationScopedRolesAsync(CancellationToken cancellationToken)
        => await db.Roles
            .Where(role => role.Scope == RoleScope.Organization)
            .OrderBy(role => role.Code)
            .ToListAsync(cancellationToken);

    /// <inheritdoc />
    public Task<Role?> FindRoleAsync(string code, CancellationToken cancellationToken)
        => db.Roles.FirstOrDefaultAsync(role => role.Code == code, cancellationToken);

    /// <inheritdoc />
    public async Task CreateOrganizationAsync(
        Organization organization,
        IReadOnlyCollection<Address> addresses,
        IReadOnlyCollection<OrganizationMember> members,
        CancellationToken cancellationToken)
    {
        // One transaction — all-or-nothing step 3. The writes are ordered by
        // hand because Organization.PrimaryAddressId → Address and
        // Address.OrganizationId → Organization form an unresolvable FK cycle
        // for a single SaveChanges (EF cannot defer either nullable FK — the
        // exactly-one-owner CHECK forbids a null-owner placeholder row). So:
        // org without its pointer, addresses (their OrganizationId now resolves),
        // the pointer UPDATE, then the member rows. Same transaction, atomic.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        Guid? primaryAddressId = organization.PrimaryAddressId;
        organization.PrimaryAddressId = null;
        db.Organizations.Add(organization);
        await db.SaveChangesAsync(cancellationToken);

        db.Addresses.AddRange(addresses);
        await db.SaveChangesAsync(cancellationToken);

        if (primaryAddressId is not null)
        {
            organization.PrimaryAddressId = primaryAddressId;
            await db.SaveChangesAsync(cancellationToken);
        }

        db.OrganizationMembers.AddRange(members);
        await db.SaveChangesAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task AddMemberAsync(OrganizationMember member, CancellationToken cancellationToken)
    {
        db.OrganizationMembers.Add(member);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<OrganizationMembership>> GetMembershipsAsync(
        Guid userId, CancellationToken cancellationToken)
    {
        var memberships = await (from member in db.OrganizationMembers
                                 where member.UserId == userId
                                 join organization in db.Organizations on member.OrganizationId equals organization.PublicId
                                 select new OrganizationMembership(
                                     organization.PublicId,
                                     organization.Name,
                                     member.RoleCode,
                                     member.InvitationStatus))
            .ToListAsync(cancellationToken);

        // Distinct + OrderBy run client-side (StringComparer is not translatable).
        return memberships
            .DistinctBy(membership => (membership.OrganizationId, membership.RoleCode))
            .OrderBy(membership => membership.OrganizationName, StringComparer.Ordinal)
            .ToList();
    }

    /// <inheritdoc />
    public Task<District?> FindDistrictAsync(string code, CancellationToken cancellationToken)
        => db.Districts.FirstOrDefaultAsync(district => district.Code == code, cancellationToken);

    /// <inheritdoc />
    public Task<Province?> FindProvinceAsync(string code, CancellationToken cancellationToken)
        => db.Provinces.FirstOrDefaultAsync(province => province.Code == code, cancellationToken);

    /// <inheritdoc />
    public Task<Country?> FindCountryAsync(string code, CancellationToken cancellationToken)
        => db.Countries.FirstOrDefaultAsync(country => country.Code == code, cancellationToken);

    // ---- User-owned addresses (/me/addresses, api.md §2.2) ------------------

    /// <inheritdoc />
    public Task<Address?> FindUserAddressAsync(Guid addressId, Guid userId, CancellationToken cancellationToken)
        => db.Addresses.FirstOrDefaultAsync(
            address => address.Id == addressId && address.UserId == userId, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<Address>> GetUserAddressesAsync(Guid userId, CancellationToken cancellationToken)
        => await db.Addresses
            .Where(address => address.UserId == userId)
            .OrderBy(address => address.Id) // Guid v7 orders by creation time
            .ToListAsync(cancellationToken);

    /// <inheritdoc />
    public async Task AddAddressAsync(Address address, CancellationToken cancellationToken)
    {
        db.Addresses.Add(address);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task UpdateAddressAsync(Address address, CancellationToken cancellationToken)
        => await db.SaveChangesAsync(cancellationToken);

    /// <inheritdoc />
    public async Task DeleteAddressAsync(Guid addressId, CancellationToken cancellationToken)
    {
        await db.Addresses
            .Where(address => address.Id == addressId)
            .ExecuteDeleteAsync(cancellationToken);
    }
}