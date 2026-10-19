namespace Stockpile.Application.Authentication;

/// <summary>
/// Public registration request. Email is the login identifier; email ownership
/// is not verified during signup.
/// </summary>
/// <param name="DraftToken">
/// The resumable registration draft this submit came from, when the visitor used
/// one (business-flows §2/§3, api.md §2.1). Completing the registration retires
/// the draft; a dead or unknown token simply expires on its own and never blocks
/// the signup.
/// </param>
public sealed record RegisterRequest(string Email, string Password, string? DraftToken = null);

/// <summary>Login request — email is the single login identifier (domain-model §2.1).</summary>
public sealed record LoginRequest(string Email, string Password);

/// <summary>
/// Password reset step 1 (task 1.37, threat-model T1.5): the email to send the
/// reset token to. Answers 202 for unknown and known accounts alike (A4).
/// </summary>
public sealed record PasswordResetRequest(string Email);

/// <summary>
/// Password reset step 2 (task 1.37): the token from the request step plus the
/// new password. The token is single-use (the security stamp rotates on
/// success) and completion revokes every session (A5); an invalid, expired,
/// re-used or cross-account token answers one identical 400 <c>INVALID_RESET_TOKEN</c>.
/// </summary>
public sealed record PasswordResetComplete(string Email, string Token, string NewPassword);

/// <summary>
/// Self-service password change (api.md §2.1): requires the current password.
/// A wrong current password is a validation failure; success keeps THIS device
/// signed in and revokes every other session.
/// </summary>
public sealed record PasswordChangeRequest(string CurrentPassword, string NewPassword);

/// <summary>
/// Email verification (task 1.38, threat-model line 169): the one-time token
/// from the verification email — nothing else. The endpoint is anonymous; the
/// token ITSELF identifies the account, so an unknown, used or expired token
/// answers one identical 400 <c>INVALID_VERIFICATION_TOKEN</c> (no enumeration).
/// </summary>
public sealed record EmailVerifyRequest(string Token);

/// <summary>
/// MFA verification (task 1.44, threat-model §13.4 line 174): the 6-digit
/// time-based code the enrolled authenticator app shows. Format is enforced at
/// the request filter AND re-checked in the use case — the filter never decides
/// MFA, only the shape.
/// </summary>
public sealed record MfaVerifyRequest(string Code);

/// <summary>Email verification succeeded (or was already true — see the note on <see cref="VerifyEmailAsync"/>).</summary>
public enum EmailVerificationOutcome
{
    /// <summary>The account was unverified; the token was consumed and <c>EmailConfirmedAt</c> set now.</summary>
    Verified,

    /// <summary>The account was already verified (a survivor row was consumed defensively).</summary>
    AlreadyVerified,
}

/// <summary>Outcome of the authenticated resend step.</summary>
public enum EmailResendOutcome
{
    /// <summary>A fresh token was minted (the outstanding one died) and the email re-queued.</summary>
    Sent,

    /// <summary>The account is already verified — nothing to resend.</summary>
    AlreadyVerified,
}