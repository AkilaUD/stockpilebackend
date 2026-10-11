using Stockpile.Domain.Enums;

namespace Stockpile.Domain.Entities.Identity;

/// <summary>
/// Per-user UI/locale preference (domain-model §2.1 child). Hard-deleted with
/// the account (§13 deletion policy).
/// </summary>
public sealed class UserPreference
{
    public Guid UserId { get; set; }

    /// <summary>Preference key, e.g. <c>density</c>, <c>resultsPerPage</c>.</summary>
    public string Key { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;
}

/// <summary>
/// Per-user, per-category, per-channel opt-in/out (domain-model §5.6 note +
/// architecture §11). Categories are notification keys; Phase 2 defines the set.
/// </summary>
public sealed class NotificationPreference
{
    public Guid UserId { get; set; }

    /// <summary>Notification category key, e.g. <c>rfq</c>, <c>quote</c>.</summary>
    public string Category { get; set; } = string.Empty;

    public NotificationChannel Channel { get; set; }

    public bool Enabled { get; set; } = true;
}
