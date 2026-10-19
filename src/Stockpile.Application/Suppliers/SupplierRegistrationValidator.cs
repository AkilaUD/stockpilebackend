using FluentValidation;

namespace Stockpile.Application.Suppliers;

/// <summary>
/// Supplier-registration field rules (api.md §2.1, domain-model §2.1/§2.3/§3.1
/// column lengths). The password's minimum length stays with the Identity
/// validators (config-driven <c>PasswordPolicy</c>) so one configuration owns
/// that rule; only "a password must be present" is checked here. Category slugs
/// are validated against the live taxonomy by the use case, because only the
/// taxonomy can answer which slugs exist.
/// </summary>
public sealed class RegisterSupplierRequestValidator : AbstractValidator<RegisterSupplierRequest>
{
    public RegisterSupplierRequestValidator()
    {
        RuleFor(request => request.Email)
            .NotEmpty().WithMessage("Email is required.")
            .EmailAddress().WithMessage("Enter a valid email address.")
            .MaximumLength(256);

        RuleFor(request => request.Password)
            .NotEmpty().WithMessage("Choose a password.");

        RuleFor(request => request.ContactFirstName)
            .NotEmpty().WithMessage("Enter the contact's first name.")
            .MaximumLength(100);

        RuleFor(request => request.ContactLastName)
            .NotEmpty().WithMessage("Enter the contact's last name.")
            .MaximumLength(100);

        RuleFor(request => request.Phone)
            .NotEmpty().WithMessage("Enter a phone number.")
            .MaximumLength(30);

        RuleFor(request => request.CompanyName)
            .NotEmpty().WithMessage("Enter your registered business name.")
            .MaximumLength(200);

        RuleFor(request => request.RegistrationNumber)
            .MaximumLength(60);

        RuleFor(request => request.Website)
            .MaximumLength(300)
            .Must(website => website is null
                || Uri.TryCreate(website, UriKind.Absolute, out Uri? uri)
                    && uri.Scheme is "https" or "http")
            .When(request => !string.IsNullOrWhiteSpace(request.Website))
            .WithMessage("Website must be an absolute http(s) URL.");

        RuleFor(request => request.Categories)
            .NotNull().WithMessage("Select at least one category you supply.")
            .Must(categories => categories is { Count: > 0 })
            .WithMessage("Select at least one category you supply.");
    }
}
