using FluentValidation;

namespace Stockpile.Application.Suppliers;

/// <summary>
/// Shape rules for the supplier's own onboarding writes (api.md §2.9). The
/// filter answers 422 with the field named (camelCased) before the use case
/// runs; the use case then normalises values and the store persists them. Only
/// *shape* belongs here — "is this a category/district we know?" is a data
/// question answered against the catalogue in the store.
/// </summary>
public sealed class UpdateSupplierProfileRequestValidator : AbstractValidator<UpdateSupplierProfileRequest>
{
    public const int MaxCompanyNameLength = 200;
    public const int MaxRegistrationNumberLength = 60;
    public const int MaxWebsiteLength = 300;
    public const int MaxContactPersonLength = 160;
    public const int MaxPhoneLength = 30;
    public const int MaxEmailLength = 160;

    public UpdateSupplierProfileRequestValidator()
    {
        // A provided company name may never be blank — blanking the supplier's
        // own display name is not a merge, it is a hole.
        RuleFor(request => request.CompanyName)
            .MaximumLength(MaxCompanyNameLength)
            .Must(name => name is null || name.Trim().Length > 0)
            .WithMessage("A company name cannot be blank.");

        RuleFor(request => request.RegistrationNumber).MaximumLength(MaxRegistrationNumberLength);
        RuleFor(request => request.Website)
            .MaximumLength(MaxWebsiteLength)
            .Must(website => website is null
                || Uri.TryCreate(website, UriKind.Absolute, out Uri? uri)
                    && uri.Scheme is "https" or "http")
            .When(request => !string.IsNullOrWhiteSpace(request.Website))
            .WithMessage("Website must be an absolute http(s) URL.");
        RuleFor(request => request.ContactPerson).MaximumLength(MaxContactPersonLength);
        RuleFor(request => request.Phone).MaximumLength(MaxPhoneLength);
        RuleFor(request => request.Email)
            .MaximumLength(MaxEmailLength)
            .EmailAddress()
            .When(request => !string.IsNullOrWhiteSpace(request.Email));
    }
}

/// <summary>Stage 5 — the slug must be present; the store decides if the taxonomy knows it.</summary>
public sealed class AddSupplierCategoryRequestValidator : AbstractValidator<AddSupplierCategoryRequest>
{
    public const int MaxSlugLength = 160;

    public AddSupplierCategoryRequestValidator() =>
        RuleFor(request => request.Slug)
            .NotEmpty()
            .MaximumLength(MaxSlugLength);
}

/// <summary>Stage 4 — the replacement set of district codes (bounded like the buyer wizard's lists).</summary>
public sealed class SetSupplierServiceAreasRequestValidator : AbstractValidator<SetSupplierServiceAreasRequest>
{
    public const int MaxDistricts = 25;
    public const int MaxDistrictCodeLength = 60;

    public SetSupplierServiceAreasRequestValidator()
    {
        RuleFor(request => request.Districts)
            .NotNull()
            .Must(districts => districts.Count <= MaxDistricts)
            .WithMessage($"Choose at most {MaxDistricts} districts.");

        RuleForEach(request => request.Districts)
            .NotEmpty()
            .MaximumLength(MaxDistrictCodeLength);

        RuleFor(request => request.DefaultDistrict).MaximumLength(MaxDistrictCodeLength);
    }
}
