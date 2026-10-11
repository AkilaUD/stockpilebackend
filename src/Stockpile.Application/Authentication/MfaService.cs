using Microsoft.AspNetCore.Identity;
using Stockpile.Application.Common.Errors;
using Stockpile.Contracts.Authentication;
using Stockpile.Contracts.Errors;
using Stockpile.Domain.Entities.Identity;

namespace Stockpile.Application.Authentication;

/// <summary>
/// Task 1.44 — the MFA-ready use cases (threat-model §13.4 line 174, T1.8,
/// api.md §2.1): enrolment mints a CSPRNG TOTP secret and hands it to the
/// owner exactly once, verification is the ONLY writer of
/// <c>TwoFactorEnabled</c>, and nothing else changes — sign-in does not
/// challenge the second factor yet, because enforcement is the §12 rollout
/// decision, not phase 1 ("architecture ready, not enforced"). Both flows run
/// on the caller's own authenticated subject, so there is no target to forge
/// (BOLA has no surface here).
/// </summary>
public sealed class MfaService(UserManager<User> userManager, TimeProvider time)
{
    /// <summary>The issuer label authenticator apps display (otpauth URI).</summary>
    public const string Issuer = "Stockpile";

    /// <summary>
    /// Mints a fresh TOTP secret and stores the account PENDING: the secret is
    /// written, <c>TwoFactorEnabled</c> flips false until a code proves the
    /// pairing. Re-enrolling (a lost device — the recovery path while no
    /// disable endpoint exists) replaces the old secret and returns the account
    /// to pending, so an unverified secret can never be the live one.
    /// </summary>
    public async Task<MfaEnrollResponse> EnrollAsync(Guid userId, CancellationToken cancellationToken)
    {
        User user = await userManager.FindByIdAsync(userId.ToString())
            ?? throw new AppException(ErrorCodes.Unauthenticated); // ticket outlived its account

        string secret = Totp.GenerateSecret();
        user.TwoFactorSecret = secret;
        user.TwoFactorEnabled = false;
        await UpdateAsync(user);

        return new MfaEnrollResponse(secret, Totp.BuildOtpauthUri(Issuer, user.Email, secret));
    }

    /// <summary>
    /// Validates a 6-digit code (RFC 6238 ±1 step, constant-time compare)
    /// against the enrolled secret and — only then — flips
    /// <c>TwoFactorEnabled</c> on. No pending enrolment is a
    /// <c>not_enrolled</c> field error, a wrong or malformed code an
    /// <c>invalid_code</c> one (both 422, field <c>code</c>); an account
    /// already enabled answers the defensive-idempotent
    /// <c>already_enabled</c>.
    /// </summary>
    public async Task<MfaVerifyResponse> VerifyAsync(Guid userId, string code, CancellationToken cancellationToken)
    {
        User user = await userManager.FindByIdAsync(userId.ToString())
            ?? throw new AppException(ErrorCodes.Unauthenticated); // ticket outlived its account

        if (user.TwoFactorEnabled)
        {
            return new MfaVerifyResponse("already_enabled");
        }

        if (user.TwoFactorSecret is null)
        {
            throw FieldFailure("not_enrolled", "No authenticator app is enrolled yet. Start enrolment first.");
        }

        if (!Totp.Verify(user.TwoFactorSecret, code, time.GetUtcNow()))
        {
            throw FieldFailure(
                "invalid_code",
                "That code is not valid. Check the time-based code in your authenticator app and try again.");
        }

        user.TwoFactorEnabled = true;
        await UpdateAsync(user);
        return new MfaVerifyResponse("enabled");
    }

    private async Task UpdateAsync(User user)
    {
        IdentityResult updated = await userManager.UpdateAsync(user);
        if (!updated.Succeeded)
        {
            // The only realistic failure is a rowversion race on the Users row
            // — surfaced as a validation envelope, never a silent partial write.
            IReadOnlyList<FieldError> fields = updated.Errors
                .Select(error => new FieldError("request", error.Code, error.Description))
                .ToList();
            throw new AppException(ErrorCodes.ValidationFailed, fields);
        }
    }

    private static AppException FieldFailure(string code, string message)
        => new(ErrorCodes.ValidationFailed, [new FieldError("code", code, message)]);
}
