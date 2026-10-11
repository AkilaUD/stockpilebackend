using Microsoft.EntityFrameworkCore;
using Stockpile.Application.Authentication;
using Stockpile.Domain.Entities.Identity;

namespace Stockpile.Infrastructure.Persistence;

/// <summary>
/// Task 1.38 — email-verification tokens over the same scoped context the
/// identity flow runs in, mirroring the reset-token ledger (1.37). The
/// anonymous verify endpoint probes by hash ALONE — the token identifies its
/// account — so the hash is uniquely indexed for a single-key probe query.
/// Writes proceed through the execute-update + SaveChanges split identical to
/// the reset store: the consume race is won in SQL, and the added row's
/// raised <c>identity.user.registered</c> event flushes with the same save.
/// </summary>
public sealed class EmailVerificationTokenStore : IEmailVerificationTokenStore
{
    private readonly StockpileDbContext _db;

    public EmailVerificationTokenStore(StockpileDbContext db) => _db = db;

    /// <inheritdoc />
    public async Task CreateAsync(EmailVerificationToken token, CancellationToken cancellationToken)
    {
        _db.EmailVerificationTokens.Add(token);

        // One live token per account: an outstanding row dies when a new one is
        // issued — the old link stops working immediately (OWASP re-issue).
        await _db.EmailVerificationTokens
            .Where(row => row.UserId == token.UserId && row.UsedAtUtc == null && row.Id != token.Id)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(row => row.UsedAtUtc, token.CreatedAtUtc),
                cancellationToken);

        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task<EmailVerificationToken?> FindPendingByHashAsync(
        string tokenHash, DateTime nowUtc, CancellationToken cancellationToken)
        => _db.EmailVerificationTokens.FirstOrDefaultAsync(
            row => row.TokenHash == tokenHash
                && row.UsedAtUtc == null && row.ExpiresAtUtc > nowUtc,
            cancellationToken);

    /// <inheritdoc />
    public Task<int> ConsumeAsync(Guid id, DateTime usedAtUtc, CancellationToken cancellationToken)
        => _db.EmailVerificationTokens
            .Where(row => row.Id == id && row.UsedAtUtc == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(row => row.UsedAtUtc, usedAtUtc),
                cancellationToken);
}