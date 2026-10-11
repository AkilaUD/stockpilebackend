using Stockpile.Domain.Common;
using Stockpile.Domain.Enums;

namespace Stockpile.Domain.Entities.Identity;

/// <summary>
/// Aggregate root (domain-model §2.1). Login is by email only; the public GUID
/// (v4 — sensitive record) never authenticates anything by itself.
/// Soft delete: <see cref="UserStatus.Deleted"/> with PII anonymised, auth trail retained.
/// </summary>
public sealed class User : AuditableEntity
{
    /// <summary>Public identifier, Guid v4 (domain-model §1 ID policy).</summary>
    public Guid PublicId { get; set; } = Guid.NewGuid();

    /// <summary>Login identifier, nvarchar(256).</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>Upper-invariant email, nvarchar(256), unique — the case-insensitive login key.</summary>
    public string EmailNormalized { get; set; } = string.Empty;

    /// <summary>Set when the verification link was consumed; null = unverified.</summary>
    public DateTime? EmailConfirmedAt { get; set; }

    /// <summary>Hashed password (nvarchar(max)). Never written to any log sink (§13.4).</summary>
    public string PasswordHash { get; set; } = string.Empty;

    public string FirstName { get; set; } = string.Empty;

    public string LastName { get; set; } = string.Empty;

    /// <summary>E.164-normalised phone, nvarchar(30)?.</summary>
    public string? Phone { get; set; }

    public DateTime? PhoneNumberConfirmedAt { get; set; }

    /// <summary>Exponential-backoff lockout end, UTC; null = not locked (§13.4).</summary>
    public DateTime? LockoutEndsAt { get; set; }

    /// <summary>Consecutive failed attempts, reset on success.</summary>
    public int FailedAccessCount { get; set; }

    /// <summary>
    /// Consecutive lockout episodes (1-based once locked): the exponent of the
    /// exponential backoff window (Auth:LockoutBaseMinutes × 2^(episode−1),
    /// jittered, capped at Auth:LockoutMaxMinutes, task 1.39). Reset on a
    /// successful sign-in, a completed password reset, or an admin unlock —
    /// so an attacker's continued failures push the window higher while the
    /// victim's own success re-arms the ladder at the base.
    /// </summary>
    public int LockoutEpisode { get; set; }

    public UserStatus Status { get; set; } = UserStatus.Active;

    /// <summary>MFA-ready architecture (§13.4); enrolment endpoints exist, enforcement is off (task 1.44).</summary>
    public bool TwoFactorEnabled { get; set; }

    /// <summary>
    /// Base32 TOTP shared secret (RFC 6238 — 20 bytes → 32 characters, task
    /// 1.44), written only by <c>POST /auth/mfa/enroll</c> and returned to the
    /// owner exactly once in the enrol response. <c>TwoFactorEnabled</c> flips
    /// true only when a code verifies against this secret
    /// (<c>POST /auth/mfa/verify</c>); re-enrolling replaces it and returns the
    /// account to pending. Never logged (§13.4) — it is the verifier's half of
    /// the second factor, not a password hash: it must stay recoverable to
    /// validate future codes, so it lives here in clear while every password
    /// and one-time token stays hashed.
    /// </summary>
    public string? TwoFactorSecret { get; set; }

    /// <summary><c>en</c> | <c>si</c> (ta reserved).</summary>
    public string PreferredLocale { get; set; } = "en";

    public DateTime? DeactivatedAt { get; set; }

    /// <summary>SQL rowversion for optimistic concurrency on this aggregate root.</summary>
    public byte[] RowVersion { get; set; } = [];
}
