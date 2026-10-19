using FluentValidation;

namespace Stockpile.Application.Suppliers;

/// <summary>
/// Shape rules for the lead actions (api.md §2.9). The filter answers 422 with
/// the field named before the use case runs; the state machine then enforces the
/// §11.2 edge itself, so a blank reason can never slip through as a decline.
/// </summary>
public sealed class DeclineSupplierLeadRequestValidator : AbstractValidator<DeclineSupplierLeadRequest>
{
    /// <summary>The <c>DeclineReason</c> column's bound (domain-model §5.4).</summary>
    public const int MaxReasonLength = 400;

    public DeclineSupplierLeadRequestValidator() =>
        RuleFor(request => request.Reason)
            .NotEmpty()
            .WithMessage("Tell the buyer why you are declining this request.")
            .MaximumLength(MaxReasonLength);
}
