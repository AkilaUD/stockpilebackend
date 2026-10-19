using FluentValidation;
using Stockpile.Contracts.Subscriptions;
using Stockpile.Domain.Enums;

namespace Stockpile.Application.Subscriptions;

public sealed class SubscribeRequestValidator : AbstractValidator<SubscribeRequest>
{
    public SubscribeRequestValidator()
    {
        RuleFor(request => request.PlanCode)
            .NotEmpty().WithMessage("Choose a plan.")
            .MaximumLength(40);
    }
}

public sealed class CancelSubscriptionRequestValidator : AbstractValidator<CancelSubscriptionRequest>
{
    public CancelSubscriptionRequestValidator()
    {
        RuleFor(request => request.Reason).MaximumLength(400);
    }
}

public sealed class MarkInvoicePaidRequestValidator : AbstractValidator<MarkInvoicePaidRequest>
{
    public MarkInvoicePaidRequestValidator()
    {
        RuleFor(request => request.Reason)
            .NotEmpty().WithMessage("Say how this invoice was settled (e.g. bank transfer received).")
            .MaximumLength(400);

        RuleFor(request => request.Reference).MaximumLength(100);
    }
}

public sealed class CreatePlanRequestValidator : AbstractValidator<CreatePlanRequest>
{
    public CreatePlanRequestValidator()
    {
        RuleFor(request => request.Code)
            .NotEmpty()
            .Matches("^[A-Za-z0-9][A-Za-z0-9-]{1,38}$")
            .WithMessage("A plan code is 2–39 letters, digits or hyphens.");

        RuleFor(request => request.Name).NotEmpty().MaximumLength(100);
        RuleFor(request => request.PriceAmount).GreaterThanOrEqualTo(0).When(request => request.PriceAmount.HasValue);
        RuleFor(request => request.Currency).NotEmpty().Matches("^[A-Za-z]{3}$").WithMessage("A currency code is three letters, e.g. LKR.");
        RuleFor(request => request.BillingPeriod)
            .Must(PlanRules.IsBillingPeriod).WithMessage("Choose Monthly, Quarterly or Yearly.");
        RuleFor(request => request.SortOrder).InclusiveBetween(0, 1000);
        RuleFor(request => request.Entitlements!).SetValidator(new EntitlementSetValidator()).When(request => request.Entitlements is not null);
    }
}

public sealed class UpdatePlanRequestValidator : AbstractValidator<UpdatePlanRequest>
{
    public UpdatePlanRequestValidator()
    {
        RuleFor(request => request.Name).NotEmpty().MaximumLength(100);
        RuleFor(request => request.PriceAmount).GreaterThanOrEqualTo(0).When(request => request.PriceAmount.HasValue);
        RuleFor(request => request.Currency).NotEmpty().Matches("^[A-Za-z]{3}$").WithMessage("A currency code is three letters, e.g. LKR.");
        RuleFor(request => request.BillingPeriod)
            .Must(PlanRules.IsBillingPeriod).WithMessage("Choose Monthly, Quarterly or Yearly.");
        RuleFor(request => request.SortOrder).InclusiveBetween(0, 1000);
        RuleFor(request => request.Entitlements!).SetValidator(new EntitlementSetValidator()).When(request => request.Entitlements is not null);
    }
}

/// <summary>A plan's entitlement set: distinct keys, well-formed rows.</summary>
internal sealed class EntitlementSetValidator : AbstractValidator<IReadOnlyList<EntitlementInput>>
{
    public EntitlementSetValidator()
    {
        RuleFor(set => set)
            .Must(set => set.Select(input => input.Key?.Trim()).Distinct(StringComparer.Ordinal).Count() == set.Count)
            .WithMessage("Each entitlement key may appear once.");

        RuleFor(set => set.Count).LessThanOrEqualTo(40).WithMessage("A plan has at most 40 entitlements.");

        RuleForEach(set => set).ChildRules(row =>
        {
            row.RuleFor(input => input.Key)
                .NotEmpty()
                .MaximumLength(40)
                .Matches("^[A-Za-z][A-Za-z0-9]*$").WithMessage("An entitlement key is letters and digits, starting with a letter.");
            row.RuleFor(input => input.Limit).GreaterThanOrEqualTo(0).When(input => input.Limit.HasValue);
            row.RuleFor(input => input)
                .Must(input => !(input.Unlimited && input.Limit.HasValue))
                .WithMessage("An unlimited entitlement has no limit.");
            row.RuleFor(input => input.Value).MaximumLength(100);
        });
    }
}

internal static class PlanRules
{
    internal static bool IsBillingPeriod(string? value)
    {
        // A named period only: Enum.TryParse would also accept "1" or "7".
        return !string.IsNullOrEmpty(value)
            && char.IsLetter(value[0])
            && Enum.TryParse(value, ignoreCase: true, out BillingPeriod parsed)
            && Enum.IsDefined(parsed);
    }
}
