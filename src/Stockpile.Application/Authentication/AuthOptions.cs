namespace Stockpile.Application.Authentication;

/// <summary>
/// Task 1.36 — configuration for the registration AND session envelope
/// (bound to the <c>Auth</c> section). The registered-user role is seeded from
/// configuration like every role (domain-model §2.2 — the seeder fails fast on
/// an unknown code, so this value is checked against live seeded roles before
/// the first account is created). The absolute session cap mirrors the
/// architecture §8.1 12-hour ceiling the cookie's sliding window can never
/// exceed; the lockout base is the exponential backoff's first rung (full
/// backoff + jitter lands with task 1.39).
/// </summary>
public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>Role assigned at registration ("buyer" by default; an org role, seeded in Seed:Roles).</summary>
    public string RegisteredUserRole { get; set; } = "buyer";

    /// <summary>
    /// Role assigned when a supplier self-registers (task 2F; "supplierOwner" by
    /// default — a Supplier-scope role seeded in Seed:Roles). Checked against the
    /// live seeded roles before the account is created, exactly like
    /// <see cref="RegisteredUserRole"/>: a misconfigured code must fail fast
    /// rather than produce a role-less supplier.
    /// </summary>
    public string RegisteredSupplierRole { get; set; } = "supplierOwner";

    /// <summary>
    /// Role the organisation creator joins with at <c>POST /organisations</c>
    /// step 3 (task 1.43): the buyer administrator who can then invite team
    /// members. Seeded from configuration like every role; the use case fails
    /// fast at first use if the code is missing or not organisation-scoped.
    /// </summary>
    public string OrganizationCreatorRole { get; set; } = "buyerAdmin";

    /// <summary>Absolute LoginSession lifetime in hours (architecture §8.1 absolute cap).</summary>
    public int AbsoluteSessionHours { get; set; } = 12;

    /// <summary>
    /// Base lockout window in minutes after MaxFailedAccessAttempts — the
    /// exponential backoff's first rung (task 1.39). A double so the Testing
    /// host can drive episode growth with a sub-minute base.
    /// </summary>
    public double LockoutBaseMinutes { get; set; } = 15;

    /// <summary>
    /// The ceiling of the exponential lockout ladder in minutes (task 1.39).
    /// Windows grow base × 2^(episode−1) with ±20 % jitter but never exceed
    /// this cap — repeated lockouts raise an alert rather than become a
    /// permanent denial (threat-model §13.4 line 168). Default 1440 (24 h).
    /// </summary>
    public int LockoutMaxMinutes { get; set; } = 1440;

    /// <summary>
    /// Password-reset token time-to-live in minutes (threat-model T1.5: "short
    /// TTL", A6). The persisted token row's <c>ExpiresAtUtc</c> is
    /// now + this window; completing after it answers the same 400 as any dead
    /// token. Validated range [1/10, 1440]: the Testing environment drives the
    /// expiry test with a sub-minute value, deployment of a value outside the
    /// range is refused at boot.
    /// </summary>
    public double PasswordResetTokenMinutes { get; set; } = 15;

    /// <summary>
    /// Email-verification token TTL in minutes (task 1.38, threat-model line 169):
    /// the persisted row's <c>ExpiresAtUtc</c> is now + this window; presenting an
    /// expired token answers the same 400 as any dead token. Default 1440 (24 h —
    /// the industry-standard window for clicking a verification link). Validated
    /// range [1/10, 10080] at boot; the Testing environment drives the expiry test
    /// with a sub-minute value.
    /// </summary>
    public double EmailVerificationTokenMinutes { get; set; } = 1440;

    /// <summary>
    /// Refresh-token TTL in minutes (task 1.40, threat-model T1.4): the persisted
    /// <c>RefreshSession</c> row's <c>ExpiresAt</c> is now + this window, always
    /// capped at its <c>LoginSession.ExpiresAt</c> (a refresh token can never
    /// outlive the session it re-establishes). Default 720 (12 h — the session's
    /// absolute ceiling, architecture §8.1). Validated range [1/10, 10080] at
    /// boot; the Testing environment drives the expiry test with a sub-minute
    /// value.
    /// </summary>
    public double RefreshTokenMinutes { get; set; } = 720;

    /// <summary>
    /// Registration-draft TTL in hours (business-flows §2/§3 "staged, resumable",
    /// api.md §2.1): the persisted draft row's <c>ExpiresAtUtc</c> is now + this
    /// window; resuming after it answers the same 404 as any dead token. Default 72
    /// (3 days — long enough to come back to a half-filled signup, short enough
    /// that abandoned prefill does not sit around). Validated range [1, 720] at boot.
    /// </summary>
    public double RegistrationDraftExpiryHours { get; set; } = 72;
}

/// <summary>
/// The <c>PasswordPolicy</c> section: Identity options are bound from here and
/// the breach-check hook is gated by <see cref="BreachCheckEnabled"/>.
/// Threat-model §4: "≥ 10 chars, no composition theatre" — length is the only
/// composition rule; the complexity flags are switched off.
/// </summary>
public sealed class PasswordPolicyOptions
{
    public const string SectionName = "PasswordPolicy";

    /// <summary>Minimum password length (threat-model §4: ≥ 10).</summary>
    public int MinimumLength { get; set; } = 10;

    /// <summary>
    /// When true the registration flow consults an <see cref="IPasswordBreachChecker"/>
    /// and rejects breached passwords. The platform refuses to boot with this
    /// flag set but no checker registered — it must never claim breach checking
    /// it does not have. The checker implementation arrives with the platform
    /// operations phase (threat-model T1.1); the hook exists from 1.36.
    /// </summary>
    public bool BreachCheckEnabled { get; set; }
}