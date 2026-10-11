using Microsoft.EntityFrameworkCore;
using Stockpile.Application.Authentication;
using Stockpile.Domain.Entities.Identity;

namespace Stockpile.Infrastructure.Persistence;

/// <summary>
/// Task 1.40 — <see cref="IRefreshSessionStore"/> over the schema's
/// <c>RefreshSessions</c> table (domain-model §2.5). Revocations are
/// set-based <c>ExecuteUpdateAsync</c> stamps on <c>RevokedAt</c> —
/// append-only facts, never deletes (X9), exactly like the LoginSession
/// store. Rotation is the one two-step write: the successor row is inserted
/// first (so the self-referencing <c>RotatedToId</c> FK always has its
/// target), then a single-row conditional update advances the old head —
/// whoever's update matches wins, a concurrent replay of the same token
/// matches nothing and the caller runs reuse detection (A9).
/// </summary>
public sealed class RefreshSessionStore(StockpileDbContext db) : IRefreshSessionStore
{
    /// <inheritdoc />
    public async Task CreateAsync(RefreshSession session, CancellationToken cancellationToken)
    {
        db.RefreshSessions.Add(session);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task<RefreshSession?> FindByHashAsync(string tokenHash, CancellationToken cancellationToken)
        => db.RefreshSessions
            .FirstOrDefaultAsync(session => session.TokenHash == tokenHash, cancellationToken);

    /// <inheritdoc />
    public async Task<RefreshSession?> RotateAsync(
        Guid currentId, RefreshSession successor, CancellationToken cancellationToken)
    {
        // Insert first: the UPDATE below writes successor.Id into a
        // constrained self-reference, so the target must already exist.
        db.RefreshSessions.Add(successor);
        await db.SaveChangesAsync(cancellationToken);

        int advanced = await db.RefreshSessions
            .Where(session => session.Id == currentId
                && session.RotatedToId == null
                && session.RevokedAt == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(session => session.RotatedToId, successor.Id),
                cancellationToken);

        if (advanced == 1)
        {
            return successor;
        }

        // Lost the race (or the chain died in between): roll the orphan back —
        // its token was never delivered, so no live credential may remain.
        db.RefreshSessions.Remove(successor);
        await db.SaveChangesAsync(cancellationToken);
        return null;
    }

    /// <inheritdoc />
    public Task<int> RevokeChainAsync(Guid sessionId, DateTime revokedAtUtc, CancellationToken cancellationToken)
        => db.RefreshSessions
            .Where(session => session.SessionId == sessionId && session.RevokedAt == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(session => session.RevokedAt, revokedAtUtc),
                cancellationToken);

    /// <inheritdoc />
    public Task<int> RevokeAllForUserAsync(Guid userId, DateTime revokedAtUtc, CancellationToken cancellationToken)
        => db.RefreshSessions
            .Where(session => session.RevokedAt == null
                && db.LoginSessions.Any(login => login.Id == session.SessionId && login.UserId == userId))
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(session => session.RevokedAt, revokedAtUtc),
                cancellationToken);

    /// <inheritdoc />
    public Task<int> RevokeOthersAsync(
        Guid userId, Guid keepSessionId, DateTime revokedAtUtc, CancellationToken cancellationToken)
        => db.RefreshSessions
            .Where(session => session.RevokedAt == null
                && session.SessionId != keepSessionId
                && db.LoginSessions.Any(login => login.Id == session.SessionId && login.UserId == userId))
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(session => session.RevokedAt, revokedAtUtc),
                cancellationToken);
}
