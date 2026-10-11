using Stockpile.Domain.Entities.Identity;

namespace Stockpile.Application.Authentication;

/// <summary>
/// Task 1.37 — the password-reset token ledger (threat-model T1.5, A5/A6).
/// Tokens are opaque 256-bit values; ONLY their SHA-256 hash is stored, so a
/// database breach yields no usable token. Rows are append-only facts: a token
/// is consumed by setting <see cref="PasswordResetToken.UsedAtUtc"/> (single-use
/// — the row, not a stateless blob, is the source of truth), never deleted (X9).
/// </summary>
public interface IPasswordResetTokenStore
{
    /// <summary>
    /// Persists a new token. Issuing a new one consumes any outstanding (unused)
    /// rows for the same user — exactly one live token per user, so an old link
    /// stops working the moment a new one is requested (OWASP "invalidate old
    /// tokens").
    /// </summary>
    Task CreateAsync(PasswordResetToken token, CancellationToken cancellationToken);

    /// <summary>
    /// The user's usable token row for the given hash, or null when the hash is
    /// unknown, already consumed, or past <paramref name="nowUtc"/> — the caller
    /// answers one identical 400 for all three (A5/A6, no reason leaks).
    /// </summary>
    Task<PasswordResetToken?> FindPendingAsync(
        Guid userId, string tokenHash, DateTime nowUtc, CancellationToken cancellationToken);

    /// <summary>
    /// Marks the row consumed (single-use, A5): 1 when THIS call won the race,
    /// 0 when it was already consumed by a concurrent completion.
    /// </summary>
    Task<int> ConsumeAsync(Guid id, DateTime usedAtUtc, CancellationToken cancellationToken);
}