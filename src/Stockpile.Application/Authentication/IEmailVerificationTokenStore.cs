using Stockpile.Domain.Entities.Identity;

namespace Stockpile.Application.Authentication;

/// <summary>
/// Task 1.38 — the email-verification token ledger (threat-model line 169,
/// security-test-matrix A11). The same discipline as the password-reset ledger
/// (1.37): opaque 256-bit tokens whose ONLY SHA-256 hash is stored — "hashed at
/// rest", so a database breach yields no usable token — rows single-use and
/// append-only facts (X9). The verify endpoint is anonymous and presents ONLY the
/// token, so lookups key on the hash alone: the token identifies its account.
/// </summary>
public interface IEmailVerificationTokenStore
{
    /// <summary>
    /// Persists a new token. Issuing one consumes any outstanding (unused) rows
    /// for the same user — exactly one live token per account, so an old link
    /// stops working the moment a new one is requested (OWASP "invalidate old
    /// tokens"; the re-issued verification email lands via the
    /// <c>identity.user.registered</c> outbox event).
    /// </summary>
    Task CreateAsync(EmailVerificationToken token, CancellationToken cancellationToken);

    /// <summary>
    /// The outstanding token row for the given hash, or null when the hash is
    /// unknown, already consumed, or past <paramref name="nowUtc"/> — the caller
    /// answers one identical 400 for all three (no reason leaks). The row, not a
    /// stateless blob, is the single-use authority.
    /// </summary>
    Task<EmailVerificationToken?> FindPendingByHashAsync(
        string tokenHash, DateTime nowUtc, CancellationToken cancellationToken);

    /// <summary>
    /// Marks the row consumed (single-use): 1 when THIS call won the race, 0 when
    /// it was already consumed by a concurrent presentation.
    /// </summary>
    Task<int> ConsumeAsync(Guid id, DateTime usedAtUtc, CancellationToken cancellationToken);
}