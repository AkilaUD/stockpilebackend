using FluentValidation;
using Stockpile.Domain.Entities.Identity;

namespace Stockpile.Application.Organizations;

/// <summary>
/// Task 1.43 — request field rules for the organisation endpoints (P9: only
/// organisation-scope roles assignable). Field structure is validated here;
/// existence facts (geography codes, accounts, duplicate membership) are
/// re-checked authoritatively by <see cref="OrganizationService"/> so a bypass
/// of the filter can never slip a platform or supplier role onto a member
/// (T6.3).
/// </summary>
public sealed class CreateOrganizationRequestValidator : AbstractValidator<CreateOrganizationRequest>
{
    public CreateOrganizationRequestValidator(
        AddressDraftValidator addressValidator,
        OrganizationMemberDraftValidator memberValidator)
    {
        RuleFor(request => request.Company).NotNull().WithMessage("Company details are required.");
        RuleFor(request => request.Company.Name)
            .NotEmpty().WithMessage("Organisation name is required.")
            .MaximumLength(200);
        RuleFor(request => request.Company.RegistrationNumber).MaximumLength(60);
        RuleFor(request => request.Company.TaxId).MaximumLength(60);
        RuleFor(request => request.Company.Website)
            .MaximumLength(300)
            .Must(website => Uri.TryCreate(website, UriKind.Absolute, out Uri? uri) && uri.Scheme is "https" or "http")
            .When(request => request.Company is not null && !string.IsNullOrWhiteSpace(request.Company.Website))
            .WithMessage("Website must be an absolute http(s) URL.");

        RuleForEach(request => request.Addresses).SetValidator(addressValidator);
        RuleForEach(request => request.Members).SetValidator(memberValidator);
    }
}

/// <summary>
/// Member entry rules (wizard step 3). The P9 gate lives here AND in
/// <see cref="CreateOrganizationMemberRequestValidator"/>: <c>RoleId</c> must
/// name a seeded organisation-scoped role, and <c>Email</c> must be a
/// well-formed address — the account's existence is the service's call.
/// </summary>
public sealed class OrganizationMemberDraftValidator : AbstractValidator<OrganizationMemberDraft>
{
    private readonly IOrganizationStore _store;

    public OrganizationMemberDraftValidator(IOrganizationStore store) => _store = store;

    public override async Task<FluentValidation.Results.ValidationResult> ValidateAsync(
        FluentValidation.ValidationContext<OrganizationMemberDraft> context,
        CancellationToken cancellation = default)
    {
        RuleFor(member => member.Email)
            .NotEmpty().WithMessage("Enter the team member's email.")
            .EmailAddress().WithMessage("Enter a valid email address.")
            .MaximumLength(256);
        RuleFor(member => member.RoleId)
            .NotEmpty().WithMessage("Choose a role for the team member.")
            .MustAsync(async (roleId, token) => await IsAssignableRoleAsync(roleId, token))
            .WithErrorCode("invalid_role_scope")
            .WithMessage("Only organisation roles can be assigned to team members.");

        return await base.ValidateAsync(context, cancellation);
    }

    /// <summary>Shared with the standalone add-member validator (P9's endpoint).</summary>
    public async Task<bool> IsAssignableRoleAsync(string roleCode, CancellationToken cancellationToken)
    {
        IReadOnlyList<Role> assignable = await _store.GetOrganizationScopedRolesAsync(cancellationToken);
        return assignable.Any(role => role.Code == roleCode);
    }
}

/// <summary>Add a member to an existing organisation (security-test-matrix P9): same email + assignable-role rules as the wizard.</summary>
public sealed class CreateOrganizationMemberRequestValidator : AbstractValidator<CreateOrganizationMemberRequest>
{
    private readonly OrganizationMemberDraftValidator _memberRules;

    public CreateOrganizationMemberRequestValidator(OrganizationMemberDraftValidator memberRules)
        => _memberRules = memberRules;

    public override async Task<FluentValidation.Results.ValidationResult> ValidateAsync(
        FluentValidation.ValidationContext<CreateOrganizationMemberRequest> context,
        CancellationToken cancellation = default)
    {
        RuleFor(request => request.Email)
            .NotEmpty().WithMessage("Enter the team member's email.")
            .EmailAddress().WithMessage("Enter a valid email address.")
            .MaximumLength(256);
        RuleFor(request => request.RoleId)
            .NotEmpty().WithMessage("Choose a role for the team member.")
            .MustAsync(async (roleId, token) => await _memberRules.IsAssignableRoleAsync(roleId, token))
            .WithErrorCode("invalid_role_scope")
            .WithMessage("Only organisation roles can be assigned to team members.");

        return await base.ValidateAsync(context, cancellation);
    }
}

/// <summary>Address field rules (domain-model §2.4 column lengths; reference-code existence is the service's call).</summary>
public sealed class AddressDraftValidator : AbstractValidator<AddressDraft>
{
    public AddressDraftValidator()
    {
        RuleFor(address => address.Line1)
            .NotEmpty().WithMessage("Address line 1 is required.")
            .MaximumLength(200);
        RuleFor(address => address.Line2).MaximumLength(200);
        RuleFor(address => address.City)
            .NotEmpty().WithMessage("City is required.")
            .MaximumLength(100);
        RuleFor(address => address.District)
            .NotEmpty().WithMessage("District is required.")
            .MaximumLength(20);
        RuleFor(address => address.Province)
            .NotEmpty().WithMessage("Province is required.")
            .MaximumLength(20);
        RuleFor(address => address.PostalCode).MaximumLength(20);
        RuleFor(address => address.CountryCode)
            .NotEmpty().WithMessage("Country is required.")
            .Length(2);
        RuleFor(address => address.Latitude)
            .InclusiveBetween(-90m, 90m)
            .When(address => address.Latitude.HasValue)
            .WithMessage("Latitude must be between -90 and 90.");
        RuleFor(address => address.Longitude)
            .InclusiveBetween(-180m, 180m)
            .When(address => address.Longitude.HasValue)
            .WithMessage("Longitude must be between -180 and 180.");
    }
}