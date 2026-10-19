using Stockpile.Contracts.Authentication;

namespace Stockpile.Application.Authentication;

/// <summary>
/// Port for the signed-in user's own profile and preferences (api.md §2.2,
/// business-flows §2). Every method is scoped to the caller's user id — another
/// user's rows are never visible (BOLA, api.md §1.6). A null return means the
/// user id does not resolve to an account (a deleted-between-token-and-call
/// race); the service turns that into the single 401 <c>UNAUTHENTICATED</c>
/// answer.
/// </summary>
public interface IUserProfileStore
{
    /// <summary>The caller's professional profile, or null when the account is gone.</summary>
    Task<ProfileResponse?> GetProfileAsync(Guid userId, CancellationToken ct);

    /// <summary>Merges the profile draft over the stored profile (partial PATCH), or null when the account is gone.</summary>
    Task<ProfileResponse?> UpdateProfileAsync(Guid userId, ProfileDraft draft, CancellationToken ct);

    /// <summary>The caller's UI/locale preferences, or null when the account is gone.</summary>
    Task<PreferencesResponse?> GetPreferencesAsync(Guid userId, CancellationToken ct);

    /// <summary>Merges the preferences draft over the stored preferences, or null when the account is gone.</summary>
    Task<PreferencesResponse?> UpdatePreferencesAsync(Guid userId, PreferencesDraft draft, CancellationToken ct);

    /// <summary>The caller's per-channel notification opt-ins, or null when the account is gone.</summary>
    Task<NotificationPreferencesResponse?> GetNotificationPreferencesAsync(Guid userId, CancellationToken ct);

    /// <summary>Merges the notification draft over the stored channels, or null when the account is gone.</summary>
    Task<NotificationPreferencesResponse?> UpdateNotificationPreferencesAsync(
        Guid userId, NotificationPreferencesDraft draft, CancellationToken ct);
}
