namespace Stockpile.Application.Outbox;

/// <summary>
/// Claims and processes one batch of pending outbox rows (architecture §10.1):
/// rows are claimed with <c>UPDLOCK, ROWLOCK, READPAST</c>, dispatched through
/// <see cref="IOutboxEventDispatcher"/>, and each row either becomes
/// <c>Processed</c> or schedules an exponential backoff (2^n seconds with
/// jitter) until the attempt ceiling (8) dead-letters it. One transaction
/// covers claim + dispatch + marks, so a crash re-runs the batch
/// (at-least-once) and a success is atomic.
/// </summary>
public interface IOutboxProcessor
{
    /// <summary>
    /// Processes up to <paramref name="batchSize"/> due rows.
    /// </summary>
    /// <returns>The number of rows claimed and processed (0 = idle).</returns>
    Task<int> ProcessBatchAsync(int batchSize, CancellationToken cancellationToken = default);
}
