using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Stockpile.Application.Outbox;
using Stockpile.Domain.Entities.Platform;
using Stockpile.Domain.Enums;
using Stockpile.Infrastructure.Persistence;

namespace Stockpile.Infrastructure.Outbox;

/// <summary>
/// <see cref="IOutboxProcessor"/> over <see cref="StockpileDbContext"/>
/// (architecture §10.1). Claims up to <paramref name="batchSize"/> due rows with
/// SQL Server's <c>UPDLOCK, ROWLOCK, READPAST</c> — the equivalent of
/// <c>SKIP LOCKED</c> — and processes them INSIDE one transaction:
///
/// * a consumer success marks the row <c>Processed</c>;
/// * a consumer failure increments <c>Attempts</c> and backs off
///   <c>2^n</c> seconds with jitter via <c>NextAttemptAt</c>;
/// * the 8th failed attempt dead-letters the row with the reason.
///
/// The claim lock rides the transaction to the commit, so concurrent Worker
/// instances never claim the same row; a process death before commit re-runs
/// the whole batch (at-least-once — consumers must be idempotent).
/// </summary>
public sealed class OutboxProcessor(
    StockpileDbContext dbContext,
    IOutboxEventDispatcher dispatcher,
    TimeProvider timeProvider,
    ILogger<OutboxProcessor> logger) : IOutboxProcessor
{
    private const int MaxAttempts = 8;
    private const int MaxDeadLetterReasonLength = 500;

    /// <inheritdoc />
    public async Task<int> ProcessBatchAsync(int batchSize, CancellationToken cancellationToken = default)
    {
        if (batchSize <= 0)
        {
            return 0;
        }

        await using IDbContextTransaction transaction =
            await dbContext.Database.BeginTransactionAsync(cancellationToken);

        // Claim query per architecture §10.1. Parameterised (X10: interpolation
        // is safe; `batchSize` is a typed int, never string interpolation of
        // untrusted input), maps back to the tracked entity.
        List<OutboxMessage> rows = await dbContext.OutboxMessages
            .FromSqlInterpolated($"""
                SELECT TOP ({batchSize}) [Id], [OccurredAt], [EventType], [Payload], [Status],
                       [Attempts], [NextAttemptAt], [CorrelationId], [DeadLetterReason]
                FROM [OutboxMessages] WITH (UPDLOCK, ROWLOCK, READPAST)
                WHERE [Status] = N'Pending' AND [NextAttemptAt] <= SYSUTCDATETIME()
                ORDER BY [Id]
                """)
            .ToListAsync(cancellationToken);

        DateTime now = timeProvider.GetUtcNow().UtcDateTime;

        foreach (OutboxMessage row in rows)
        {
            try
            {
                await dispatcher.DispatchAsync(new OutboxDelivery(row.EventType, row.Payload), cancellationToken);
                row.Status = OutboxMessageStatus.Processed;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                row.Attempts += 1;

                if (row.Attempts >= MaxAttempts)
                {
                    row.Status = OutboxMessageStatus.DeadLettered;
                    row.DeadLetterReason = exception.Message.Length <= MaxDeadLetterReasonLength
                        ? exception.Message
                        : exception.Message[..MaxDeadLetterReasonLength];
                    OutboxLog.DeadLettered(logger, row.Id, row.EventType, row.Attempts, row.DeadLetterReason);
                }
                else
                {
                    row.NextAttemptAt = now.Add(Backoff(row.Attempts));
                    OutboxLog.RetryScheduled(logger, row.Id, row.EventType, row.Attempts, row.NextAttemptAt);
                }
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return rows.Count;
    }

    /// <summary>Exponential backoff: 2^n seconds plus 0–999 ms jitter (§10.1).</summary>
    private static TimeSpan Backoff(int attempts) =>
        TimeSpan.FromSeconds(Math.Pow(2, attempts))
            .Add(TimeSpan.FromMilliseconds(Random.Shared.Next(0, 1000)));
}
