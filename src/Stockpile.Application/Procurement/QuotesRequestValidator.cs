using FluentValidation;
using Stockpile.Contracts.Procurement;

namespace Stockpile.Application.Procurement;

/// <summary>
/// Field rules for <c>POST /quotes</c> (api.md §2.7, business-flows §7).
/// Structure only — every fact that needs the RFQ (does this line belong to the
/// RFQ, does the declared coverage match the lines, is the lead still answerable)
/// is re-checked authoritatively by <see cref="QuotesService"/>, so a bypass of the
/// action filter can never persist a bad quote.
/// <para>
/// The two conditions the API names its own errors for — an empty coverage and a
/// missing validity — are deliberately NOT validated here: the service raises
/// <c>QUOTE_COVERAGE_EMPTY</c> / <c>QUOTE_VALIDITY_REQUIRED</c> so a client sees
/// the specific code the reference documents rather than the generic envelope.
/// </para>
/// </summary>
public sealed class CreateQuoteRequestValidator : AbstractValidator<CreateQuoteRequest>
{
    public CreateQuoteRequestValidator()
    {
        RuleFor(request => request.RfqMatchId)
            .NotEmpty().WithMessage("Choose the lead you are quoting.");

        RuleFor(request => request.Coverage)
            .Must(value => value is not null && QuoteWireNames.Coverage.ContainsKey(value))
            .WithMessage("Choose Full or Partial coverage.");

        RuleFor(request => request.Currency)
            .NotEmpty().WithMessage("Choose a quoting currency.")
            .Matches("^[A-Za-z]{3}$").WithMessage("A currency code is three letters, e.g. LKR.");

        RuleFor(request => request.TaxState)
            .Must(value => value is not null && QuoteWireNames.TaxStates.ContainsKey(value))
            .WithMessage("Choose TaxIncluded, TaxExcluded or TaxExempt.");

        RuleFor(request => request.LeadTimeDays)
            .GreaterThanOrEqualTo(0)
            .When(request => request.LeadTimeDays.HasValue)
            .WithMessage("Lead time cannot be negative.");

        RuleFor(request => request.DeliveryCost)
            .GreaterThanOrEqualTo(0)
            .When(request => request.DeliveryCost.HasValue)
            .WithMessage("Delivery cost cannot be negative.");

        RuleFor(request => request.DiscountPercent)
            .InclusiveBetween(0, 100)
            .When(request => request.DiscountPercent.HasValue)
            .WithMessage("A discount is a percentage between 0 and 100.");

        RuleFor(request => request.PaymentTerms).MaximumLength(300);
        RuleFor(request => request.DeliveryTerms).MaximumLength(300);
        RuleFor(request => request.PackSizeNote).MaximumLength(300);
        RuleFor(request => request.MoqNote).MaximumLength(300);
        RuleFor(request => request.TechnicalNotes).MaximumLength(2000);
        RuleFor(request => request.SupplierNote).MaximumLength(1000);

        RuleFor(request => request.Items)
            .NotNull().WithMessage("Add at least one line item.");

        RuleFor(request => request.Items)
            .Must(items => items.Select(item => item.RfqItemId).Distinct().Count() == items.Count)
            .When(request => request.Items is not null)
            .WithMessage("Each RFQ line can be quoted once.");

        RuleForEach(request => request.Items)
            .SetValidator(new CreateQuoteItemRequestValidator());
    }
}

/// <summary>One quoted line's structural rules (api.md §2.7, domain-model §6.2).</summary>
public sealed class CreateQuoteItemRequestValidator : AbstractValidator<CreateQuoteItemRequest>
{
    public CreateQuoteItemRequestValidator()
    {
        RuleFor(item => item.RfqItemId)
            .NotEmpty().WithMessage("Each line must name the RFQ line it answers.");

        RuleFor(item => item.Quantity)
            .GreaterThan(0).WithMessage("Quote a quantity greater than zero.");

        RuleFor(item => item.UnitPrice)
            .GreaterThanOrEqualTo(0).WithMessage("Unit price cannot be negative.");

        RuleFor(item => item.SubstitutionNote).MaximumLength(500);
    }
}
