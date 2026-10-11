using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Stockpile.Application.Idempotency;
using Stockpile.Domain.Entities.Platform;
using Stockpile.Infrastructure.Persistence;

namespace Stockpile.Infrastructure.Idempotency;

/// <summary>
/// <see cref="IIdempotencyStore"/> over the request-scoped
/// <see cref="StockpileDbContext"/>. Atomicity (architecture §9.4 step 3) comes
/// from the ambient transaction: the key row is added to the SAME scoped
/// context the payload uses, <paramref name="execute"/> runs inside a
/// <c>BeginTransaction</c>, its SaveChanges flushes the key row alongside the
/// operation's own writes, and a single commit publishes both — so a crashed
/// or failed request leaves no half-written key and is safe to retry.
/// </summary>
public sealed class IdempotencyStore(StockpileDbContext dbContext, TimeProvider timeProvider) : IIdempotencyStore
{
    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    /// <inheritdoc />
    public async Task<IdempotencyResult> ExecuteAsync(
        Guid ownerId,
        string key,
        string requestHash,
        Func<CancellationToken, Task<(int Status, string? Reference)>> execute,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(execute);
        ValidateKey(key);

        // Fast path: an existing key adjudicates without any transaction.
        IdempotencyKey? existing = await FindAsync(ownerId, key, cancellationToken);
        if (existing is not null)
        {
            return Adjudicate(existing, requestHash);
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        // Re-check inside the transaction: the insert below serializes on the
        // composite primary key either way, but this makes the common race
        // resolve without relying on the exception path.
        IdempotencyKey? lostRace = await FindAsync(ownerId, key, cancellationToken);
        if (lostRace is not null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Adjudicate(lostRace, requestHash);
        }

        var row = new IdempotencyKey
        {
            OwnerId = ownerId,
            Key = key,
            RequestHash = requestHash,
            ExpiresAt = timeProvider.GetUtcNow().UtcDateTime.Add(Retention),
        };
        dbContext.IdempotencyKeys.Add(row);

        try
        {
            (int Status, string? Reference) outcome = await execute(cancellationToken);
            row.ResponseStatus = outcome.Status;
            row.ResponseReference = outcome.Reference;

            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new IdempotencyResult(IdempotencyResultKind.Processed, outcome.Status, outcome.Reference);
        }
        catch (DbUpdateException exception) when (IsUniqueViolationOnIdempotencyKey(exception))
        {
            // A concurrent twin committed the same key; the winner's row decides.
            await transaction.RollbackAsync(cancellationToken);
            IdempotencyKey? winner = await FindAsync(ownerId, key, cancellationToken);
            return winner is null
                ? new IdempotencyResult(IdempotencyResultKind.KeyConflict)
                : Adjudicate(winner, requestHash);
        }
    }

    private async Task<IdempotencyKey?> FindAsync(Guid ownerId, string key, CancellationToken cancellationToken) =>
        await dbContext.IdempotencyKeys
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.OwnerId == ownerId && x.Key == key, cancellationToken);

    private static IdempotencyResult Adjudicate(IdempotencyKey row, string requestHash) =>
        string.Equals(row.RequestHash, requestHash, StringComparison.Ordinal)
            ? new IdempotencyResult(IdempotencyResultKind.Replayed, row.ResponseStatus, row.ResponseReference)
            : new IdempotencyResult(IdempotencyResultKind.KeyConflict);

    /// <summary>
    /// Only a unique violation on OUR key row may trigger re-adjudication; a
    /// unique violation from the payload's own writes must propagate as a 500,
    /// never be mistaken for an idempotent replay. The constraint name embeds
    /// the table name (e.g. <c>PK_IdempotencyKeys</c>), which the message test
    /// relies on.
    /// </summary>
    private static bool IsUniqueViolationOnIdempotencyKey(DbUpdateException exception)
    {
        var sql = FindInner<SqlException>(exception);
        return sql is not null
            && sql.Number is 2601 or 2627
            && sql.Message.Contains("IdempotencyKeys", StringComparison.OrdinalIgnoreCase);
    }

    private static TException? FindInner<TException>(Exception? exception)
        where TException : Exception
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is TException match)
            {
                return match;
            }
        }

        return null;
    }

    private static void ValidateKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (key.Length > 100)
        {
            // nvarchar(100) column — guard against a storage failure before it
            // becomes a 500 after the payload already ran.
            throw new ArgumentOutOfRangeException(nameof(key), "Idempotency-Key exceeds the 100-character limit.");
        }
    }
}
