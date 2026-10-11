using Microsoft.EntityFrameworkCore;
using Stockpile.Application.Authentication;
using Stockpile.Domain.Entities.Identity;
using Stockpile.Domain.Enums;
using Stockpile.Infrastructure.Persistence;

namespace Stockpile.Infrastructure.Authentication;

/// <summary>
/// Task 1.36 — <see cref="ILoginSessionStore"/> over the schema's
/// <c>LoginSessions</c> table (domain-model §2.5). Revocations are
/// set-based <c>ExecuteUpdateAsync</c> on the <c>RevokedAt</c> column —
/// append-only facts, never deletes (X9). <c>TouchAsync</c> is throttled by
/// the caller (the cookie hook runs on every request; a heartbeat per request
/// would be write traffic for no information).
/// </summary>
public sealed class LoginSessionStore(StockpileDbContext db) : ILoginSessionStore
{
    /// <inheritdoc />
    public async Task<LoginSession> CreateAsync(
        Guid userId, TimeSpan lifetime, string? ipAddress, string? userAgent, CancellationToken cancellationToken)
    {
        DateTime nowUtc = DateTime.UtcNow;
        var session = new LoginSession
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            IssuedAt = nowUtc,
            ExpiresAt = nowUtc + lifetime,
            LastSeenAt = nowUtc,
            IpAddress = ipAddress,
            UserAgent = userAgent,
        };

        db.LoginSessions.Add(session);
        await db.SaveChangesAsync(cancellationToken);
        return session;
    }

    /// <inheritdoc />
    public Task<LoginSession?> FindAsync(Guid sessionId, CancellationToken cancellationToken)
        => db.LoginSessions.FirstOrDefaultAsync(session => session.Id == sessionId, cancellationToken);

    /// <inheritdoc />
    public Task TouchAsync(Guid sessionId, DateTime nowUtc, TimeSpan throttle, CancellationToken cancellationToken)
        => db.LoginSessions
            .Where(session => session.Id == sessionId && session.LastSeenAt < nowUtc - throttle)
            .ExecuteUpdateAsync(setters => setters.SetProperty(session => session.LastSeenAt, nowUtc), cancellationToken);

    /// <inheritdoc />
    public Task RevokeAsync(Guid sessionId, DateTime revokedAtUtc, CancellationToken cancellationToken)
        => db.LoginSessions
            .Where(session => session.Id == sessionId && session.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(session => session.RevokedAt, revokedAtUtc), cancellationToken);

    /// <inheritdoc />
    public Task<int> RevokeAllAsync(Guid userId, DateTime revokedAtUtc, CancellationToken cancellationToken)
        => db.LoginSessions
            .Where(session => session.UserId == userId && session.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(session => session.RevokedAt, revokedAtUtc), cancellationToken);

    /// <inheritdoc />
    public Task<int> RevokeOthersAsync(
        Guid userId, Guid keepSessionId, DateTime revokedAtUtc, CancellationToken cancellationToken)
        => db.LoginSessions
            .Where(session => session.UserId == userId
                && session.Id != keepSessionId
                && session.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(session => session.RevokedAt, revokedAtUtc), cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<LoginSession>> ListActiveAsync(
        Guid userId, DateTime nowUtc, CancellationToken cancellationToken)
        => await db.LoginSessions
            .Where(session => session.UserId == userId
                && session.RevokedAt == null
                && session.ExpiresAt > nowUtc)
            .OrderByDescending(session => session.LastSeenAt)
            .ThenByDescending(session => session.IssuedAt)
            .ToListAsync(cancellationToken);
}

/// <summary>
/// Task 1.36 — <see cref="IUserPermissionStore"/> over the schema: the union of
/// every <c>RolePermission</c> grant on every role the user holds
/// (domain-model §2.2). A user holds roles two ways: identity bindings
/// (<c>UserRoles</c> — the registered-platform roles) and accepted
/// organisation memberships (<c>OrganizationMembers</c> rows task 1.43), whose
/// role grants resolve here too — so an organisation administrator's
/// <c>organization.member.manage</c> capability appears exactly when their
/// membership is accepted, never on a Pending invitation. Distinct and
/// ordinal-ordered for stable response payloads. A permission grants capability
/// only — object access is confirmed separately (architecture §8.2, layer 4).
/// </summary>
public sealed class UserPermissionStore(StockpileDbContext db) : IUserPermissionStore
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetPermissionCodesAsync(
        Guid userId, CancellationToken cancellationToken)
        // Distinct + OrderBy run client-side: SQL Server cannot be asked to
        // sort with a StringComparer (the comparer is not translatable).
        => (await (from userRole in db.UserRoles
                   where userRole.UserId == userId
                   join grant in db.RolePermissions on userRole.RoleCode equals grant.RoleCode
                   select grant.PermissionCode)
            .Union(
                // Accepted memberships only: a Pending invitation grants nothing.
                from member in db.OrganizationMembers
                where member.UserId == userId && member.InvitationStatus == InvitationStatus.Accepted
                join grant in db.RolePermissions on member.RoleCode equals grant.RoleCode
                select grant.PermissionCode)
            .Distinct()
            .ToListAsync(cancellationToken))
            .Order(StringComparer.Ordinal)
            .ToList();
}