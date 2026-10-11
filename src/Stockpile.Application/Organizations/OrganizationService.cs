using Microsoft.Extensions.Options;
using Stockpile.Application.Audit;
using Stockpile.Application.Authentication;
using Stockpile.Application.Common.Errors;
using Stockpile.Contracts.Errors;
using Stockpile.Contracts.Organizations;
using Stockpile.Domain.Entities.Identity;
using Stockpile.Domain.Entities.Reference;
using Stockpile.Domain.Enums;

namespace Stockpile.Application.Organizations;

/// <summary>
/// Task 1.43 — the organisation use cases (implementation-plan 1.43, P9):
/// organisation creation (business-flows §2 step 3), team invitations whose
/// only non-creator path to <c>OrganizationMember</c> is a Pending invitation
/// (T6.4), user-owned address CRUD (api.md §2.2), and the membership payload
/// the session endpoint surfaces. Membership checks are the layer-3 binding
/// promised in 1.41: the caller's <i>capability</i> (a permission policy) and
/// the caller's <i>membership</i> of the specific organisation are judged
/// separately — a non-member holding the capability still gets a BOLA 404
/// (api.md §1.6, architecture §8.2).
/// </summary>
public sealed class OrganizationService(
    IOrganizationStore store,
    IOptions<AuthOptions> authOptions,
    IAuditService audit)
{
    /// <summary>
    /// Creates the buyer organisation for the staged registration (business-flows
    /// §2 step 3): one atomic write of the organisation, its addresses, the
    /// caller's own accepted membership (the configured creator role), and any
    /// invited team members as Pending rows. Validation beyond the request
    /// filter — geography reference codes, account existence, role scope, the
    /// creator-role configuration — happens here, the authoritative layer.
    /// </summary>
    public async Task<OrganizationResponse> CreateOrganizationAsync(
        Guid actorId, CreateOrganizationRequest request, CancellationToken cancellationToken)
    {
        List<FieldError> errors = [];

        List<Address> addresses = [];
        if (request.Addresses is not null)
        {
            for (int i = 0; i < request.Addresses.Count; i++)
            {
                AddressDraft draft = request.Addresses[i];
                Address? address = await TryBuildAddressAsync(draft, $"addresses[{i}].", errors, cancellationToken);
                if (address is not null)
                {
                    addresses.Add(address);
                }
            }
        }

        // The creator-role configuration is resolved and validated here (like
        // Auth:RegisteredUserRole at registration): a misconfigured or retired
        // role must fail loudly at first use, never halfway through a create.
        Role? creatorRole = await store.FindRoleAsync(authOptions.Value.OrganizationCreatorRole, cancellationToken);
        if (creatorRole is null || creatorRole.Scope != RoleScope.Organization)
        {
            throw new InvalidOperationException(
                $"Auth:OrganizationCreatorRole '{authOptions.Value.OrganizationCreatorRole}' is not a seeded " +
                "organisation-scope role. Roles are seeded from configuration (domain-model §2.2); seed one.");
        }

        User caller = await store.FindUserByIdAsync(actorId, cancellationToken)
            ?? throw new AppException(ErrorCodes.Unauthenticated); // ticket outlived its account: behave as anonymous

        List<OrganizationMember> invited = [];
        if (request.Members is not null)
        {
            for (int i = 0; i < request.Members.Count; i++)
            {
                OrganizationMemberDraft draft = request.Members[i];
                bool roleAssignable = await IsAssignableOrganizationRoleAsync(draft.RoleId, cancellationToken);
                if (!roleAssignable)
                {
                    errors.Add(NotAssignableRoleFieldError($"members[{i}].roleId"));
                    continue;
                }

                User? invitee = await store.FindUserByNormalizedEmailAsync(
                    NormalizeEmail(draft.Email), cancellationToken);
                if (invitee is null)
                {
                    errors.Add(new FieldError(
                        $"members[{i}].email", "not_registered", "No Stockpile account matches this email address."));
                    continue;
                }

                if (invitee.PublicId == actorId)
                {
                    errors.Add(new FieldError(
                        $"members[{i}].email", "already_member", "The organisation creator is already a member."));
                    continue;
                }

                invited.Add(new OrganizationMember
                {
                    OrganizationId = Guid.Empty, // assigned below once the org id exists
                    UserId = invitee.PublicId,
                    RoleCode = draft.RoleId,
                    InvitationStatus = InvitationStatus.Pending,
                    JoinedAt = null,
                });
            }
        }

        if (errors.Count > 0)
        {
            throw new AppException(ErrorCodes.ValidationFailed, errors);
        }

        var organization = new Organization
        {
            PublicId = Guid.CreateVersion7(),
            Name = request.Company.Name.Trim(),
            Type = OrganizationType.BuyerCompany,  // buyer-registration wizard (business-flows §2)
            RegistrationNumber = Trim(request.Company.RegistrationNumber),
            TaxId = Trim(request.Company.TaxId),
            Website = Trim(request.Company.Website),
            Status = OrganizationStatus.Active,
            PrimaryAddressId = addresses.FirstOrDefault()?.Id, // wizard: first address is the primary (domain-model §2.3)
        };

        // Wizard addresses belong to the new organisation — exactly one owner,
        // enforced by CHECK (domain-model §2.4).
        foreach (Address address in addresses)
        {
            address.OrganizationId = organization.PublicId;
        }

        foreach (OrganizationMember member in invited)
        {
            member.OrganizationId = organization.PublicId;
        }

        OrganizationMember creatorRow = new()
        {
            OrganizationId = organization.PublicId,
            UserId = actorId,
            RoleCode = creatorRole.Code,
            InvitationStatus = InvitationStatus.Accepted,
            JoinedAt = DateTimeOffset.UtcNow.UtcDateTime,
        };

        List<OrganizationMember> allMembers = [creatorRow, .. invited];
        await store.CreateOrganizationAsync(organization, addresses, allMembers, cancellationToken);
        await audit.RecordAsync(new AuditEntry(
            "organization.created", "Organization", organization.PublicId.ToString(), AuditResult.Success),
            cancellationToken);

        // Invited rows are appended for every validated member entry, so the
        // responses stay index-aligned with request.Members (any invalid entry
        // threw above). The creator rides first, accepted from the start.
        List<OrganizationMemberResponse> members = BuildMemberResponses(invited, request, caller, creatorRow);

        return new OrganizationResponse(
            organization.PublicId.ToString(),
            organization.Name,
            organization.Type.ToString(),
            organization.Status.ToString(),
            organization.RegistrationNumber,
            organization.TaxId,
            organization.Website,
            addresses.Select(ToResponse).ToList(),
            members);
    }

    private static List<OrganizationMemberResponse> BuildMemberResponses(
        List<OrganizationMember> invited,
        CreateOrganizationRequest request,
        User caller,
        OrganizationMember creatorRow)
    {
        IReadOnlyList<OrganizationMemberDraft> drafts = request.Members ?? [];
        var responses = new List<OrganizationMemberResponse>(invited.Count + 1)
        {
            ToMemberResponse(caller, creatorRow),
        };
        for (int i = 0; i < invited.Count; i++)
        {
            OrganizationMember row = invited[i];
            responses.Add(new OrganizationMemberResponse(
                row.UserId.ToString(),
                drafts[i].Email,
                row.RoleCode,
                row.InvitationStatus.ToString(),
                row.JoinedAt?.ToString("O", System.Globalization.CultureInfo.InvariantCulture)));
        }

        return responses;
    }

    /// <summary>
    /// Add a team member (security-test-matrix P9): the caller must be an
    /// accepted member of the organisation (layer 3) — otherwise the BOLA 404
    /// hides the target's existence — and the requested role must be an
    /// organisation-scope role, re-checked here even though the request filter
    /// enforces it (T6.3, defense in depth).
    /// </summary>
    public async Task<OrganizationMemberResponse> AddMemberAsync(
        Guid actorId, Guid organizationId, CreateOrganizationMemberRequest request, CancellationToken cancellationToken)
    {
        Organization? organization = await store.FindByIdAsync(organizationId, cancellationToken);
        OrganizationMember? callerMembership = organization is null
            ? null
            : await store.FindMemberAsync(organization.PublicId, actorId, cancellationToken);
        if (organization is null
            || callerMembership is null
            || callerMembership.InvitationStatus != InvitationStatus.Accepted)
        {
            // api.md §1.6 BOLA: existence is not inferable across tenants.
            throw new AppException(ErrorCodes.ResourceNotFound);
        }

        Role? role = await store.FindRoleAsync(request.RoleId, cancellationToken);
        if (role is null || role.Scope != RoleScope.Organization)
        {
            throw new AppException(ErrorCodes.ValidationFailed, [NotAssignableRoleFieldError("roleId")]);
        }

        User? invitee = await store.FindUserByNormalizedEmailAsync(NormalizeEmail(request.Email), cancellationToken);
        if (invitee is null)
        {
            throw new AppException(ErrorCodes.ValidationFailed,
                [new FieldError("email", "not_registered", "No Stockpile account matches this email address.")]);
        }

        OrganizationMember? existing = await store.FindMemberAsync(organization.PublicId, invitee.PublicId, cancellationToken);
        if (existing is not null && existing.RoleCode == request.RoleId)
        {
            throw new AppException(ErrorCodes.ValidationFailed,
                [new FieldError("email", "already_member", "This person already has that role in this organisation.")]);
        }

        var row = new OrganizationMember
        {
            OrganizationId = organization.PublicId,
            UserId = invitee.PublicId,
            RoleCode = request.RoleId,
            InvitationStatus = InvitationStatus.Pending,
            JoinedAt = null,
        };
        await store.AddMemberAsync(row, cancellationToken);
        await audit.RecordAsync(new AuditEntry(
            "organization.member.invited", "Organization", organization.PublicId.ToString(), AuditResult.Success),
            cancellationToken);

        return ToMemberResponse(invitee, row);
    }

    // ---- /me/addresses (api.md §2.2) ----------------------------------------

    public async Task<IReadOnlyList<AddressResponse>> GetAddressesAsync(
        Guid actorId, CancellationToken cancellationToken)
        => (await store.GetUserAddressesAsync(actorId, cancellationToken))
            .Select(ToResponse)
            .ToList();

    public async Task<AddressResponse> CreateAddressAsync(
        Guid actorId, AddressDraft draft, CancellationToken cancellationToken)
    {
        List<FieldError> errors = await ValidateGeographyAsync(draft, string.Empty, cancellationToken);
        if (errors.Count > 0)
        {
            throw new AppException(ErrorCodes.ValidationFailed, errors);
        }

        var address = BuildAddress(draft);
        address.UserId = actorId;
        await store.AddAddressAsync(address, cancellationToken);
        return ToResponse(address);
    }

    public async Task<AddressResponse> UpdateAddressAsync(
        Guid actorId, Guid addressId, AddressDraft draft, CancellationToken cancellationToken)
    {
        Address? address = await store.FindUserAddressAsync(addressId, actorId, cancellationToken)
            ?? throw new AppException(ErrorCodes.ResourceNotFound); // another user's address is invisible (BOLA)

        List<FieldError> errors = await ValidateGeographyAsync(draft, string.Empty, cancellationToken);
        if (errors.Count > 0)
        {
            throw new AppException(ErrorCodes.ValidationFailed, errors);
        }

        Apply(address, draft);
        await store.UpdateAddressAsync(address, cancellationToken);
        return ToResponse(address);
    }

    public async Task DeleteAddressAsync(Guid actorId, Guid addressId, CancellationToken cancellationToken)
    {
        Address? address = await store.FindUserAddressAsync(addressId, actorId, cancellationToken)
            ?? throw new AppException(ErrorCodes.ResourceNotFound);
        await store.DeleteAddressAsync(address.Id, cancellationToken);
    }

    // ---- Session payload (GET /auth/session, api.md §2.1) -------------------

    /// <summary>The caller's accepted memberships (api.md §2.1: organisation memberships on the identity payload).</summary>
    public async Task<IReadOnlyList<OrganizationMembership>> GetAcceptedMembershipsAsync(
        Guid userId, CancellationToken cancellationToken)
        => (await store.GetMembershipsAsync(userId, cancellationToken))
            .Where(membership => membership.Status == InvitationStatus.Accepted)
            .ToList();

    // ---- Internals ----------------------------------------------------------

    private async Task<bool> IsAssignableOrganizationRoleAsync(string roleCode, CancellationToken cancellationToken)
        => (await store.GetOrganizationScopedRolesAsync(cancellationToken))
            .Any(role => role.Code == roleCode);

    private static FieldError NotAssignableRoleFieldError(string field) =>
        new(field, "invalid_role_scope", "Only organisation roles can be assigned to team members.");

    /// <summary>Builds a validated address entity, collecting geography reference errors into <paramref name="errors"/>.</summary>
    private async Task<Address?> TryBuildAddressAsync(
        AddressDraft draft, string fieldPrefix, List<FieldError> errors, CancellationToken cancellationToken)
    {
        List<FieldError> geography = await ValidateGeographyAsync(draft, fieldPrefix, cancellationToken);
        if (geography.Count > 0)
        {
            errors.AddRange(geography);
            return null;
        }

        return BuildAddress(draft);
    }

    private async Task<List<FieldError>> ValidateGeographyAsync(
        AddressDraft draft, string fieldPrefix, CancellationToken cancellationToken)
    {
        List<FieldError> errors = [];

        District? district = await store.FindDistrictAsync(draft.District, cancellationToken);
        if (district is null)
        {
            errors.Add(new FieldError(fieldPrefix + "district", "invalid_reference", "Unknown district code."));
        }

        Province? province = await store.FindProvinceAsync(draft.Province, cancellationToken);
        if (province is null)
        {
            errors.Add(new FieldError(fieldPrefix + "province", "invalid_reference", "Unknown province code."));
        }

        Country? country = await store.FindCountryAsync(draft.CountryCode, cancellationToken);
        if (country is null)
        {
            errors.Add(new FieldError(fieldPrefix + "countryCode", "invalid_reference", "Unknown country code."));
        }

        if (district is not null
            && province is not null
            && !string.Equals(district.ProvinceCode, draft.Province, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(new FieldError(
                fieldPrefix + "province", "district_province_mismatch", "The district does not belong to that province."));
        }

        return errors;
    }

    private static Address BuildAddress(AddressDraft draft) => new()
    {
        Id = Guid.CreateVersion7(),
        Line1 = draft.Line1.Trim(),
        Line2 = Trim(draft.Line2),
        City = draft.City.Trim(),
        District = draft.District,
        Province = draft.Province,
        PostalCode = Trim(draft.PostalCode),
        CountryCode = draft.CountryCode,
        Latitude = draft.Latitude,
        Longitude = draft.Longitude,
        IsDefaultBilling = draft.IsDefaultBilling,
        IsDefaultDelivery = draft.IsDefaultDelivery,
    };

    private static void Apply(Address address, AddressDraft draft)
    {
        address.Line1 = draft.Line1.Trim();
        address.Line2 = Trim(draft.Line2);
        address.City = draft.City.Trim();
        address.District = draft.District;
        address.Province = draft.Province;
        address.PostalCode = Trim(draft.PostalCode);
        address.CountryCode = draft.CountryCode;
        address.Latitude = draft.Latitude;
        address.Longitude = draft.Longitude;
        address.IsDefaultBilling = draft.IsDefaultBilling;
        address.IsDefaultDelivery = draft.IsDefaultDelivery;
    }

    private static string NormalizeEmail(string email) => email.Trim().ToUpperInvariant();

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static AddressResponse ToResponse(Address address) => new(
        address.Id.ToString(),
        address.Line1,
        address.Line2,
        address.City,
        address.District,
        address.Province,
        address.PostalCode,
        address.CountryCode,
        address.Latitude,
        address.Longitude,
        address.IsDefaultBilling,
        address.IsDefaultDelivery);

    private static OrganizationMemberResponse ToMemberResponse(User user, OrganizationMember row) => new(
        user.PublicId.ToString(),
        user.Email,
        row.RoleCode,
        row.InvitationStatus.ToString(),
        row.JoinedAt?.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
}