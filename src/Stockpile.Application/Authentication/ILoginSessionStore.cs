using Stockpile.Domain.Entities.Identity;

namespace Stockpile.Application.Authentication;

/// <summary>
/// Task 1.36 — server-side session persistence (domain-model §2.5). Sessions
/// exist so they are revocable now, not merely expired later: login creates a
/// row, the cookie validation hook checks it on every request, and logout /
/// logout-all / session-revoke mark it revoked. Rows are append-only facts —
/// revocation writes <c>RevokedAt</c>, it never deletes the row (auth trail,
/// X9).
/// </summary>
public interface ILoginSessionStore
{
    /// <summary>Creates a session with an absolute <paramref name="lifetime"/> (architecture §8.1 cap).</summary>
    Task<LoginSession> CreateAsync(
        Guid userId, TimeSpan lifetime, string? ipAddress, string? userAgent, CancellationToken cancellationToken);

    /// <summary>Finds a session by id; null when unknown (never a throw — the cookie hook treats missing as expired).</summary>
    Task<LoginSession?> FindAsync(Guid sessionId, CancellationToken cancellationToken);

    /// <summary>
    /// Sliding-heartbeat marker (domain-model §2.5): bumps <c>LastSeenAt</c>
    /// only when older than <paramref name="throttle"/> — the cookie hook runs
    /// on every request, and a heartbeat per request would be write traffic for
    /// no information.
    /// </summary>
    Task TouchAsync(Guid sessionId, DateTime nowUtc, TimeSpan throttle, CancellationToken cancellationToken);

    /// <summary>Marks one session revoked (idempotent).</summary>
    Task RevokeAsync(Guid sessionId, DateTime revokedAtUtc, CancellationToken cancellationToken);

    /// <summary>Revokes every live session of the user (logout-all, §13.4); returns the count revoked.</summary>
    Task<int> RevokeAllAsync(Guid userId, DateTime revokedAtUtc, CancellationToken cancellationToken);

    /// <summary>
    /// Revokes every live session EXCEPT <paramref name="keepSessionId"/> (the
    /// password-change device stays signed in); returns the count revoked.
    /// </summary>
    Task<int> RevokeOthersAsync(
        Guid userId, Guid keepSessionId, DateTime revokedAtUtc, CancellationToken cancellationToken);

    /// <summary>Live sessions (not revoked, not expired), newest activity first.</summary>
    Task<IReadOnlyList<LoginSession>> ListActiveAsync(Guid userId, DateTime nowUtc, CancellationToken cancellationToken);
}

/// <summary>
/// Resolves a user's effective permission codes (domain-model §2.2): the union
/// of every grant on every role the user holds. Used by the identity payload
/// (<c>/auth/session</c>) from 1.36; the authorization pipeline (1.41/1.42)
/// reuses the same resolution server-side rather than trusting issued claims.
/// A permission grants capability only — object access is confirmed separately
/// (architecture §8.2).
/// </summary>
public interface IUserPermissionStore
{
    Task<IReadOnlyList<string>> GetPermissionCodesAsync(Guid userId, CancellationToken cancellationToken);
}