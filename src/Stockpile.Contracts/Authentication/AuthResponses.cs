namespace Stockpile.Contracts.Authentication;

/// <summary>
/// The authenticated principal's identity payload (api.md §2.1
/// <c>GET /auth/session</c> and the login response): everything the SPA needs
/// to bootstrap — profile, verification state, roles, resolved permissions,
/// and organisation memberships. Serialized camelCase; optional members are
/// omitted when absent, never null (api.md §1.4).
/// </summary>
public sealed record AuthSessionResponse(
    string PublicId,
    string Email,
    string FirstName,
    string LastName,
    string PreferredLocale,
    bool EmailConfirmed,
    string Status,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<OrganizationMembershipResponse> OrgMemberships);

/// <summary>One <c>OrganizationMember</c> row shown to its own member (domain-model §2.3).</summary>
public sealed record OrganizationMembershipResponse(
    string OrganizationId,
    string Name,
    string Role);

/// <summary>One active server-side session (domain-model §2.5) in <c>GET /auth/sessions</c>.</summary>
public sealed record ActiveSessionResponse(
    string Id,
    bool IsCurrent,
    DateTime IssuedAt,
    DateTime ExpiresAt,
    DateTime LastSeenAt,
    string? IpAddress,
    string? UserAgent);

/// <summary>The session directory: active sessions, newest activity first, current marked (api.md §2.1).</summary>
public sealed record SessionsListResponse(
    IReadOnlyList<ActiveSessionResponse> Items,
    string CurrentSessionId);

/// <summary>
/// Registration acknowledgment (api.md §2.1): the account exists, verification
/// is pending, and the antiforgery double-submit cookie was just stored — the
/// <c>requestToken</c> echoes it so the SPA can make its first unsafe verb
/// without a separate minting round-trip (§1.10).
/// </summary>
public sealed record RegisterResponse(string Status, string RequestToken);

/// <summary>
/// Login acknowledgment (api.md §1.10/§2.1): session + antiforgery cookies are
/// set on the response; the body carries the minted request token and the
/// freshly-authenticated identity so the SPA skips a second round-trip.
/// </summary>
public sealed record LoginResponse(string RequestToken, AuthSessionResponse User);

/// <summary>
/// Refresh acknowledgment (task 1.40, api.md §2.1): the response rotates the
/// <c>.Stockpile.Refresh</c> cookie (Set-Cookie), re-signs the session cookie,
/// and re-mints antiforgery — the body mirrors login's shape so the SPA treats
/// a refresh exactly like a silent re-sign-in. A dead token answers 401
/// <c>SESSION_EXPIRED</c> instead (A8/A9).
/// </summary>
public sealed record RefreshResponse(string RequestToken, AuthSessionResponse User);

/// <summary>
/// Task 1.36 — the antiforgery minting response (api.md §1.10): a fresh
/// double-submit token for the CURRENT principal (anonymous when no ticket is
/// presented). The SPA primes with GET <c>/auth/antiforgery</c> before its
/// first write and re-primes after logout, when the ticket (and therefore the
/// token's embedded identity claim) changes.
/// </summary>
public sealed record AntiforgeryTokenResponse(string RequestToken);

/// <summary>
/// Password reset step 1 acknowledgment (task 1.37, A4): ALWAYS 202 with an
/// identical shape. Known accounts receive a real reset token; unknown
/// accounts a high-entropy inert one — a caller can never distinguish the two.
/// The token rides both this response (the synchronous next step) and the
/// <c>identity.password.reset_requested</c> outbox payload for the transactional
/// reset email (1.38); both channels carry the same single-use value.
/// </summary>
public sealed record PasswordResetRequestedResponse(string Status, string RequestToken);

/// <summary>Password reset step 2 acknowledgment (task 1.37, A5/A6).</summary>
public sealed record PasswordResetCompleteResponse(string Status);

/// <summary>
/// Email verification acknowledgment (task 1.38, A11): <c>"verified"</c> consumes
/// the one-time token and confirms the address; <c>"already_verified"</c> is the
/// defensive-idempotency answer for a click that arrives after verification. Never
/// carries the token — the verification email's outbox payload is the delivery
/// channel, and the anonymous verify request presents it, not the response.
/// </summary>
public sealed record EmailVerifyResponse(string Status);

/// <summary>
/// MFA enrolment response (task 1.44, api.md §2.1): the freshly minted TOTP
/// secret and the otpauth URI the authenticator app scans. This is the ONLY
/// place the secret ever appears — it is never logged and never readable again
/// (re-enrolling mints a new one); <c>TwoFactorEnabled</c> stays false until a
/// code from the paired app verifies against this secret.
/// </summary>
public sealed record MfaEnrollResponse(string Secret, string OtpauthUri);

/// <summary>
/// MFA verification acknowledgment (task 1.44): <c>"enabled"</c> flipped
/// <c>TwoFactorEnabled</c> on for the first time; <c>"already_enabled"</c> is
/// the defensive-idempotency answer when the account was verified before.
/// </summary>
public sealed record MfaVerifyResponse(string Status);