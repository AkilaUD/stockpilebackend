using FluentValidation;
using Stockpile.Contracts.Authentication;

namespace Stockpile.Application.Authentication;

/// <summary>
/// Wizard step 2 field rules (business-flows §2, api.md §2.2). The role is the
/// closed set the wizard offers; list entries are bounded so the key/value store
/// cannot be flooded through the profile endpoint. Enforced at the request
/// filter (task 1.21) — the use case still normalises (trim, dedupe) before it
/// persists.
/// </summary>
public sealed class ProfileDraftValidator : AbstractValidator<ProfileDraft>
{
    /// <summary>The role codes the wizard offers (business-flows §2 step 2).</summary>
    public static readonly IReadOnlyList<string> Roles =
    [
        "procurement-manager",
        "project-manager",
        "owner-director",
        "other",
    ];

    /// <summary>Upper bound on each list — matches what the wizard can reasonably collect.</summary>
    public const int MaxListEntries = 25;

    /// <summary>Longest single list entry; keeps one preference row well inside nvarchar(400).</summary>
    public const int MaxEntryLength = 120;

    public ProfileDraftValidator()
    {
        RuleFor(draft => draft.Role)
            .MaximumLength(60)
            .Must(role => string.IsNullOrEmpty(role) || Roles.Contains(role))
            .WithMessage("Choose one of the listed roles.")
            .WithErrorCode("invalid_value");

        RuleFor(draft => draft.ProcurementLocations)
            .Must(list => list is null || list.Count <= MaxListEntries)
            .WithMessage($"Choose at most {MaxListEntries} procurement locations.")
            .WithErrorCode("too_many");
        RuleForEach(draft => draft.ProcurementLocations)
            .NotEmpty().WithMessage("Procurement locations cannot be blank.")
            .MaximumLength(MaxEntryLength);

        RuleFor(draft => draft.ProjectInterests)
            .Must(list => list is null || list.Count <= MaxListEntries)
            .WithMessage($"Choose at most {MaxListEntries} project interests.")
            .WithErrorCode("too_many");
        RuleForEach(draft => draft.ProjectInterests)
            .NotEmpty().WithMessage("Project interests cannot be blank.")
            .MaximumLength(MaxEntryLength);
    }
}

/// <summary>
/// Wizard step 4a field rules (api.md §2.2): the locale must be one the frontend
/// actually ships (<c>en</c>, <c>si</c>). Nothing else in this draft is
/// constrained — a null member simply leaves the stored value alone.
/// </summary>
public sealed class PreferencesDraftValidator : AbstractValidator<PreferencesDraft>
{
    /// <summary>Locales the frontend ships (<c>messages/en.json</c>, <c>messages/si.json</c>).</summary>
    public static readonly IReadOnlyList<string> Locales = ["en", "si"];

    public PreferencesDraftValidator()
    {
        RuleFor(draft => draft.Locale)
            .Must(locale => string.IsNullOrEmpty(locale) || Locales.Contains(locale))
            .WithMessage("Choose a supported language.")
            .WithErrorCode("invalid_value");
    }
}
