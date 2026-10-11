using Microsoft.Extensions.Logging;

namespace Stockpile.Infrastructure.Outbox;

/// <summary>
/// Source-generated structured logging for the outbox pipeline. Payloads are
/// never logged (they may carry PII); only event type + row metadata.
/// </summary>
internal static partial class OutboxLog
{
    [LoggerMessage(EventId = 2300, Level = LogLevel.Warning,
        Message = "Outbox event '{EventType}' has no registered consumer yet (architecture §10.2)")]
    public static partial void NoConsumer(ILogger logger, string eventType);

    [LoggerMessage(EventId = 2301, Level = LogLevel.Warning,
        Message = "Outbox row {RowId} ('{EventType}') attempt {Attempts} failed; next attempt {NextAttemptAt:O}")]
    public static partial void RetryScheduled(ILogger logger, long rowId, string eventType, int attempts, DateTime nextAttemptAt);

    [LoggerMessage(EventId = 2302, Level = LogLevel.Error,
        Message = "Outbox row {RowId} ('{EventType}') dead-lettered after {Attempts} attempts: {Reason}")]
    public static partial void DeadLettered(ILogger logger, long rowId, string eventType, int attempts, string reason);
}
