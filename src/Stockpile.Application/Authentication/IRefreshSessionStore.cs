using Stockpile.Domain.Entities.Identity;

namespace Stockpile.Application.Authentication;

/// <summary>
/// Task 1.40 — the rotating refresh-token ledger (domain-model §2.5,
/// threat-model T1.4, security-test-matrix A8/A9). Tokens are opaque 256-bit
/// values; ONLY their SHA-256 hash is stored (unique index), so a database
/// breach yields no usable token — the same "hashed at rest" discipline as
/// the password-reset and verification ledgers. Rows are append-only facts
/// (X9): rotation advances the chain by pointing <see cref="RefreshSession.RotatedToId"/>
/// at a successor row, revocation stamps <see cref="RefreshSession.RevokedAt"/>,
/// and nothing is ever deleted.
/// </summary>
public interface IRefreshSessionStore
{
    /// <summary>Persists a freshly minted token row (login issue or rotation successor).</summary>
    Task CreateAsync(RefreshSession session, CancellationToken cancellationToken);

    /// <summary>The row for the given hash, or null when unknown — the caller answers one identical 401 for every dead state.</summary>
    Task<RefreshSession?> FindByHashAsync(string tokenHash, CancellationToken cancellationToken);

    /// <summary>
    /// Advances the chain: inserts <paramref name="successor"/> and points the
    /// still-live <paramref name="currentId"/> row at it, atomically guarded by
    /// <c>RotatedToId IS NULL AND RevokedAt IS NULL</c>. Returns the successor
    /// when THIS call won the race, null when the token was rotated or revoked
    /// concurrently — the caller then runs reuse detection (A9).
    /// </summary>
    Task<RefreshSession?> RotateAsync(
        Guid currentId, RefreshSession successor, CancellationToken cancellationToken);

    /// <summary>Stamps every refresh row of one session (logout, revoke, reuse detection): the number of rows that were still live.</summary>
    Task<int> RevokeChainAsync(Guid sessionId, DateTime revokedAtUtc, CancellationToken cancellationToken);

    /// <summary>Stamps every refresh row across all of a user's sessions (logout-all, password reset).</summary>
    Task<int> RevokeAllForUserAsync(Guid userId, DateTime revokedAtUtc, CancellationToken cancellationToken);

    /// <summary>Stamps every refresh row of the user's sessions EXCEPT one (password change keeps this device).</summary>
    Task<int> RevokeOthersAsync(
        Guid userId, Guid keepSessionId, DateTime revokedAtUtc, CancellationToken cancellationToken);
}
