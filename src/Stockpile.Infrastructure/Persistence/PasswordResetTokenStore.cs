using Microsoft.EntityFrameworkCore;
using Stockpile.Application.Authentication;
using Stockpile.Domain.Entities.Identity;

namespace Stockpile.Infrastructure.Persistence;

/// <summary>
/// Task 1.37 — password-reset tokens over the same scoped context the identity
/// flow runs in (the completion share's one context with UserManager, so its
/// writes join the same unit of work). Lookups are by (user, hash) — the hash
/// is the key, never the token.
/// </summary>
public sealed class PasswordResetTokenStore : IPasswordResetTokenStore
{
    private readonly StockpileDbContext _db;

    public PasswordResetTokenStore(StockpileDbContext db) => _db = db;

    /// <inheritdoc />
    public async Task CreateAsync(PasswordResetToken token, CancellationToken cancellationToken)
    {
        _db.PasswordResetTokens.Add(token);

        // One live token per user: an outstanding row dies when a new one is
        // issued — the old link stops working immediately (OWASP).
        await _db.PasswordResetTokens
            .Where(row => row.UserId == token.UserId && row.UsedAtUtc == null && row.Id != token.Id)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(row => row.UsedAtUtc, token.CreatedAtUtc),
                cancellationToken);

        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task<PasswordResetToken?> FindPendingAsync(
        Guid userId, string tokenHash, DateTime nowUtc, CancellationToken cancellationToken)
        => _db.PasswordResetTokens.FirstOrDefaultAsync(
            row => row.UserId == userId && row.TokenHash == tokenHash
                && row.UsedAtUtc == null && row.ExpiresAtUtc > nowUtc,
            cancellationToken);

    /// <inheritdoc />
    public Task<int> ConsumeAsync(Guid id, DateTime usedAtUtc, CancellationToken cancellationToken)
        => _db.PasswordResetTokens
            .Where(row => row.Id == id && row.UsedAtUtc == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(row => row.UsedAtUtc, usedAtUtc),
                cancellationToken);
}