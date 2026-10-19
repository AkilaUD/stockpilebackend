using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Stockpile.Application.Authentication;
using Stockpile.Contracts.Authentication;
using Stockpile.Domain.Entities.Identity;
using Stockpile.Domain.Enums;

namespace Stockpile.Infrastructure.Persistence.Stores;

/// <summary>
/// EF implementation of <see cref="IUserProfileStore"/> (business-flows §2,
/// api.md §2.2). The professional profile and the quote-notification flag live in
/// the per-user <see cref="UserPreference"/> key/value table — one row per scalar,
/// with list entries keyed by a zero-padded index so ordering survives a
/// round-trip without needing a schema change. The UI locale lives on
/// <see cref="User.PreferredLocale"/>; per-channel opt-ins live in
/// <see cref="NotificationPreference"/>. Every query is scoped by the caller's
/// user id, so one principal can never read or write another's rows (BOLA,
/// api.md §1.6).
/// </summary>
public sealed class UserProfileStore(StockpileDbContext db) : IUserProfileStore
{
    private const string RoleKey = "profile.role";
    private const string LocationPrefix = "profile.location.";
    private const string InterestPrefix = "profile.interest.";
    private const string QuoteNotificationsKey = "notifications.quote";

    /// <summary>Notification category the wizard's channel switches govern (api.md §2.2 — RFQ/quote notices).</summary>
    private const string NotificationCategory = "rfq";

    /// <inheritdoc />
    public async Task<ProfileResponse?> GetProfileAsync(Guid userId, CancellationToken ct)
    {
        if (!await db.Users.AnyAsync(user => user.PublicId == userId, ct))
        {
            return null;
        }

        List<UserPreference> rows = await ProfileRowsAsync(userId, ct);
        return new ProfileResponse(
            rows.FirstOrDefault(row => row.Key == RoleKey)?.Value,
            Values(rows, LocationPrefix),
            Values(rows, InterestPrefix));
    }

    /// <inheritdoc />
    public async Task<ProfileResponse?> UpdateProfileAsync(Guid userId, ProfileDraft draft, CancellationToken ct)
    {
        if (!await db.Users.AnyAsync(user => user.PublicId == userId, ct))
        {
            return null;
        }

        bool roleChanged = draft.Role is not null;
        bool locationsChanged = draft.ProcurementLocations is not null;
        bool interestsChanged = draft.ProjectInterests is not null;

        if (roleChanged || locationsChanged || interestsChanged)
        {
            // ExecuteDelete runs immediately, so the rows we are about to
            // re-insert cannot collide with the ones being removed (the PK is
            // (UserId, Key) and the indexed keys repeat across writes).
            await db.UserPreferences
                .Where(row => row.UserId == userId
                    && ((roleChanged && row.Key == RoleKey)
                        || (locationsChanged && row.Key.StartsWith(LocationPrefix))
                        || (interestsChanged && row.Key.StartsWith(InterestPrefix))))
                .ExecuteDeleteAsync(ct);

            if (roleChanged && !string.IsNullOrEmpty(draft.Role))
            {
                db.UserPreferences.Add(new UserPreference { UserId = userId, Key = RoleKey, Value = draft.Role });
            }

            if (locationsChanged)
            {
                AddIndexed(userId, LocationPrefix, draft.ProcurementLocations!);
            }

            if (interestsChanged)
            {
                AddIndexed(userId, InterestPrefix, draft.ProjectInterests!);
            }

            await db.SaveChangesAsync(ct);
        }

        return await GetProfileAsync(userId, ct);
    }

    /// <inheritdoc />
    public async Task<PreferencesResponse?> GetPreferencesAsync(Guid userId, CancellationToken ct)
    {
        User? user = await db.Users.AsNoTracking().FirstOrDefaultAsync(candidate => candidate.PublicId == userId, ct);
        if (user is null)
        {
            return null;
        }

        UserPreference? quote = await db.UserPreferences.AsNoTracking()
            .FirstOrDefaultAsync(row => row.UserId == userId && row.Key == QuoteNotificationsKey, ct);

        return new PreferencesResponse(user.PreferredLocale, QuoteEnabled(quote));
    }

    /// <inheritdoc />
    public async Task<PreferencesResponse?> UpdatePreferencesAsync(
        Guid userId, PreferencesDraft draft, CancellationToken ct)
    {
        User? user = await db.Users.FirstOrDefaultAsync(candidate => candidate.PublicId == userId, ct);
        if (user is null)
        {
            return null;
        }

        if (draft.Locale is not null)
        {
            user.PreferredLocale = draft.Locale;
        }

        if (draft.QuoteNotificationPrefs is not null)
        {
            string value = draft.QuoteNotificationPrefs.Value ? "true" : "false";
            UserPreference? row = await db.UserPreferences
                .FirstOrDefaultAsync(candidate => candidate.UserId == userId && candidate.Key == QuoteNotificationsKey, ct);
            if (row is null)
            {
                db.UserPreferences.Add(new UserPreference
                {
                    UserId = userId,
                    Key = QuoteNotificationsKey,
                    Value = value,
                });
            }
            else
            {
                row.Value = value;
            }
        }

        await db.SaveChangesAsync(ct);
        return new PreferencesResponse(user.PreferredLocale, draft.QuoteNotificationPrefs ?? await QuoteFlagAsync(userId, ct));
    }

    /// <inheritdoc />
    public async Task<NotificationPreferencesResponse?> GetNotificationPreferencesAsync(
        Guid userId, CancellationToken ct)
    {
        if (!await db.Users.AnyAsync(user => user.PublicId == userId, ct))
        {
            return null;
        }

        List<NotificationPreference> rows = await ChannelRowsAsync(userId, ct);
        return new NotificationPreferencesResponse(
            Channel(rows, NotificationChannel.Email, fallback: true),
            Channel(rows, NotificationChannel.Sms, fallback: false),
            Channel(rows, NotificationChannel.WhatsApp, fallback: false));
    }

    /// <inheritdoc />
    public async Task<NotificationPreferencesResponse?> UpdateNotificationPreferencesAsync(
        Guid userId, NotificationPreferencesDraft draft, CancellationToken ct)
    {
        if (!await db.Users.AnyAsync(user => user.PublicId == userId, ct))
        {
            return null;
        }

        List<NotificationPreference> rows = await ChannelRowsAsync(userId, ct);
        ApplyChannel(rows, userId, NotificationChannel.Email, draft.Email);
        ApplyChannel(rows, userId, NotificationChannel.Sms, draft.Sms);
        ApplyChannel(rows, userId, NotificationChannel.WhatsApp, draft.WhatsApp);
        await db.SaveChangesAsync(ct);

        return await GetNotificationPreferencesAsync(userId, ct);
    }

    // ---- Internals ----------------------------------------------------------

    private Task<List<UserPreference>> ProfileRowsAsync(Guid userId, CancellationToken ct) =>
        db.UserPreferences.AsNoTracking()
            .Where(row => row.UserId == userId
                && (row.Key == RoleKey
                    || row.Key.StartsWith(LocationPrefix)
                    || row.Key.StartsWith(InterestPrefix)))
            .ToListAsync(ct);

    private Task<List<NotificationPreference>> ChannelRowsAsync(Guid userId, CancellationToken ct) =>
        db.NotificationPreferences
            .Where(row => row.UserId == userId && row.Category == NotificationCategory)
            .ToListAsync(ct);

    private async Task<bool> QuoteFlagAsync(Guid userId, CancellationToken ct)
    {
        UserPreference? quote = await db.UserPreferences.AsNoTracking()
            .FirstOrDefaultAsync(row => row.UserId == userId && row.Key == QuoteNotificationsKey, ct);
        return QuoteEnabled(quote);
    }

    /// <summary>Absent or unparsable flag → opted in (matches the wizard default and the channel default).</summary>
    private static bool QuoteEnabled(UserPreference? row) =>
        row is null || !bool.TryParse(row.Value, out bool enabled) || enabled;

    private static bool Channel(List<NotificationPreference> rows, NotificationChannel channel, bool fallback) =>
        rows.FirstOrDefault(row => row.Channel == channel)?.Enabled ?? fallback;

    private void ApplyChannel(
        List<NotificationPreference> rows, Guid userId, NotificationChannel channel, bool? enabled)
    {
        if (enabled is null)
        {
            return;
        }

        NotificationPreference? row = rows.FirstOrDefault(candidate => candidate.Channel == channel);
        if (row is null)
        {
            db.NotificationPreferences.Add(new NotificationPreference
            {
                UserId = userId,
                Category = NotificationCategory,
                Channel = channel,
                Enabled = enabled.Value,
            });
        }
        else
        {
            row.Enabled = enabled.Value;
        }
    }

    private void AddIndexed(Guid userId, string prefix, IReadOnlyList<string> values)
    {
        for (int i = 0; i < values.Count; i++)
        {
            db.UserPreferences.Add(new UserPreference
            {
                UserId = userId,
                Key = prefix + i.ToString("D3", CultureInfo.InvariantCulture),
                Value = values[i],
            });
        }
    }

    private static List<string> Values(List<UserPreference> rows, string prefix) =>
        rows.Where(row => row.Key.StartsWith(prefix, StringComparison.Ordinal))
            .OrderBy(row => row.Key, StringComparer.Ordinal)
            .Select(row => row.Value)
            .ToList();
}
