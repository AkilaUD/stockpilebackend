using FluentValidation;
using Stockpile.Contracts.Procurement;

namespace Stockpile.Application.Procurement;

/// <summary>
/// Field rules for <c>POST /rfqs</c> (domain-model §5.3 invariants). Structure
/// only — existence facts (an unknown department slug, an unknown unit code)
/// are re-checked authoritatively by <see cref="RfqService"/> so a bypass of the
/// action filter can never persist a bad line.
/// </summary>
public sealed class CreateRfqRequestValidator : AbstractValidator<CreateRfqRequest>
{
    public CreateRfqRequestValidator()
    {
        RuleFor(request => request.Title)
            .NotEmpty().WithMessage("Give your request a title.")
            .MaximumLength(300);

        RuleFor(request => request.DeliveryLocationText).MaximumLength(300);
        RuleFor(request => request.SpecialInstructions).MaximumLength(2000);
        RuleFor(request => request.ExpiryDays)
            .InclusiveBetween(1, 90)
            .When(request => request.ExpiryDays.HasValue)
            .WithMessage("Quotes are accepted for 1 to 90 days.");

        RuleFor(request => request.Items)
            .NotEmpty().WithMessage("Add at least one line item.");

        RuleForEach(request => request.Items).SetValidator(new CreateRfqItemRequestValidator());

        RuleFor(request => request.Company!.Name)
            .NotEmpty().WithMessage("Company name is required.")
            .MaximumLength(200)
            .When(request => request.Company is not null);
        RuleFor(request => request.Company!.RegistrationNumber)
            .MaximumLength(60)
            .When(request => request.Company is not null);
        RuleFor(request => request.Company!.Website)
            .MaximumLength(300)
            .Must(website => Uri.TryCreate(website, UriKind.Absolute, out Uri? uri) && uri.Scheme is "https" or "http")
            .When(request => request.Company is not null && !string.IsNullOrWhiteSpace(request.Company.Website))
            .WithMessage("Website must be an absolute http(s) URL.");
    }
}

/// <summary>One line's rules: a meaningful description, a positive quantity, and length caps.</summary>
public sealed class CreateRfqItemRequestValidator : AbstractValidator<CreateRfqItemRequest>
{
    public CreateRfqItemRequestValidator()
    {
        RuleFor(item => item.Description)
            .NotEmpty().WithMessage("Describe what you need.")
            .MinimumLength(8).WithMessage("Describe what you need in a little more detail.")
            .MaximumLength(500);
        RuleFor(item => item.Quantity).GreaterThan(0).WithMessage("Quantity must be greater than zero.");
        RuleFor(item => item.Specification).MaximumLength(500);
        RuleFor(item => item.CategorySlug).MaximumLength(400);
        RuleFor(item => item.UnitOfMeasureCode).MaximumLength(80);
    }
}
