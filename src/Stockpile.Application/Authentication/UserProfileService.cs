using Stockpile.Application.Audit;
using Stockpile.Application.Common.Errors;
using Stockpile.Contracts.Authentication;
using Stockpile.Contracts.Errors;
using Stockpile.Domain.Enums;

namespace Stockpile.Application.Authentication;

/// <summary>
/// Use cases for the signed-in user's own profile and preferences
/// (business-flows §2 steps 2 and 4, api.md §2.2). Mirrors the
/// organisation/address pattern: the request filter validates the shape
/// (<see cref="ProfileDraftValidator"/>, <see cref="PreferencesDraftValidator"/>),
/// this service normalises the payload and records the audit trail, and the
/// store persists it. Every write is scoped to the caller's user id (BOLA,
/// api.md §1.6); a user id that no longer resolves is the single 401
/// <c>UNAUTHENTICATED</c> answer.
/// </summary>
public sealed class UserProfileService(
    IUserProfileStore store,
    IAuditService audit)
{
    /// <summary>Gets the caller's professional profile (wizard step 2 read).</summary>
    public async Task<ProfileResponse> GetProfileAsync(Guid userId, CancellationToken ct)
        => await store.GetProfileAsync(userId, ct) ?? throw Unauthenticated();

    /// <summary>Merges the caller's professional profile (wizard step 2 submit).</summary>
    public async Task<ProfileResponse> UpdateProfileAsync(Guid userId, ProfileDraft draft, CancellationToken ct)
    {
        ProfileDraft normalized = new(
            Normalize(draft.Role),
            NormalizeList(draft.ProcurementLocations),
            NormalizeList(draft.ProjectInterests));

        ProfileResponse response = await store.UpdateProfileAsync(userId, normalized, ct) ?? throw Unauthenticated();
        await audit.RecordAsync(
            new AuditEntry("user.profile.updated", "User", userId.ToString(), AuditResult.Success), ct);
        return response;
    }

    /// <summary>Gets the caller's UI/locale preferences (wizard step 4a read).</summary>
    public async Task<PreferencesResponse> GetPreferencesAsync(Guid userId, CancellationToken ct)
        => await store.GetPreferencesAsync(userId, ct) ?? throw Unauthenticated();

    /// <summary>Merges the caller's UI/locale preferences (wizard step 4a submit).</summary>
    public async Task<PreferencesResponse> UpdatePreferencesAsync(
        Guid userId, PreferencesDraft draft, CancellationToken ct)
    {
        PreferencesResponse response = await store.UpdatePreferencesAsync(userId, draft, ct) ?? throw Unauthenticated();
        await audit.RecordAsync(
            new AuditEntry("user.preferences.updated", "User", userId.ToString(), AuditResult.Success), ct);
        return response;
    }

    /// <summary>Gets the caller's per-channel notification opt-ins (wizard step 4b read).</summary>
    public async Task<NotificationPreferencesResponse> GetNotificationPreferencesAsync(
        Guid userId, CancellationToken ct)
        => await store.GetNotificationPreferencesAsync(userId, ct) ?? throw Unauthenticated();

    /// <summary>Merges the caller's per-channel notification opt-ins (wizard step 4b submit).</summary>
    public async Task<NotificationPreferencesResponse> UpdateNotificationPreferencesAsync(
        Guid userId, NotificationPreferencesDraft draft, CancellationToken ct)
    {
        NotificationPreferencesResponse response =
            await store.UpdateNotificationPreferencesAsync(userId, draft, ct) ?? throw Unauthenticated();
        await audit.RecordAsync(
            new AuditEntry("user.notification-preferences.updated", "User", userId.ToString(), AuditResult.Success), ct);
        return response;
    }

    private static AppException Unauthenticated() => new(ErrorCodes.Unauthenticated);

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Trims, drops blanks, and removes duplicates (ordinal, first occurrence wins) so a messy client payload still persists cleanly.</summary>
    private static List<string>? NormalizeList(IReadOnlyList<string>? values)
    {
        if (values is null)
        {
            return null;
        }

        List<string> normalized = [];
        foreach (string value in values)
        {
            string trimmed = value.Trim();
            if (trimmed.Length == 0 || normalized.Contains(trimmed, StringComparer.Ordinal))
            {
                continue;
            }

            normalized.Add(trimmed);
        }

        return normalized;
    }
}
