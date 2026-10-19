namespace Stockpile.Contracts.Authentication;

/// <summary>
/// Wizard step 2 — the professional profile draft (business-flows §2, api.md
/// §2.2). <c>PATCH</c> merges per member: a null member leaves the stored value
/// untouched, an empty list clears that list, and an empty <c>Role</c> string
/// clears the role. Every member is optional so a step can be completed in any
/// order and re-entered later (business-flows §2: each step is independently
/// idempotent and resumable).
/// </summary>
public sealed record ProfileDraft(
    string? Role,
    IReadOnlyList<string>? ProcurementLocations,
    IReadOnlyList<string>? ProjectInterests);

/// <summary>Wizard step 2 read shape. Lists are never null — an unset profile reads back as two empty lists.</summary>
public sealed record ProfileResponse(
    string? Role,
    IReadOnlyList<string> ProcurementLocations,
    IReadOnlyList<string> ProjectInterests);

/// <summary>
/// Wizard step 4a — UI/locale preferences (api.md §2.2). Partial merge: a null
/// member leaves the stored value untouched.
/// </summary>
public sealed record PreferencesDraft(
    string? Locale,
    bool? QuoteNotificationPrefs);

/// <summary>
/// Wizard step 4a read shape. <c>Locale</c> reflects <c>User.PreferredLocale</c>;
/// <c>QuoteNotificationPrefs</c> defaults to <c>true</c> until the buyer opts out
/// (the whole point of requesting a quote is hearing back).
/// </summary>
public sealed record PreferencesResponse(
    string Locale,
    bool QuoteNotificationPrefs);

/// <summary>
/// Wizard step 4b — per-channel notification opt-ins (api.md §2.2). Partial
/// merge: a null member leaves the stored channel untouched.
/// </summary>
public sealed record NotificationPreferencesDraft(
    bool? Email,
    bool? Sms,
    bool? WhatsApp);

/// <summary>
/// Wizard step 4b read shape. Defaults: email on, SMS and WhatsApp off until the
/// account opts in. Wire name of <c>WhatsApp</c> is camelCase <c>whatsApp</c>.
/// </summary>
public sealed record NotificationPreferencesResponse(
    bool Email,
    bool Sms,
    bool WhatsApp);
