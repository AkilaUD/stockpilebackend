using Stockpile.Domain.Enums;

namespace Stockpile.Domain.Entities.Platform;

/// <summary>
/// Transactional outbox row (architecture §10.1): written in the same EF
/// transaction as the aggregate change, claimed by the Worker with
/// UPDLOCK/ROWLOCK/READPAST, retried 2^n seconds with jitter to 8 attempts,
/// then dead-lettered.
/// </summary>
public sealed class OutboxMessage
{
    public long Id { get; set; }

    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;

    /// <summary>Dotted event type, e.g. <c>rfq.submitted</c> (architecture §10.2 catalogue).</summary>
    public string EventType { get; set; } = string.Empty;

    /// <summary>Serialised event payload, JSON (nvarchar(max)).</summary>
    public string Payload { get; set; } = string.Empty;

    public OutboxMessageStatus Status { get; set; } = OutboxMessageStatus.Pending;

    public int Attempts { get; set; }

    /// <summary>Earliest time the claim query may pick this row up; equals OccurredAt on insert.</summary>
    public DateTime NextAttemptAt { get; set; } = DateTime.UtcNow;

    /// <summary>Request correlation id, propagated to the Worker (architecture §14).</summary>
    public string? CorrelationId { get; set; }

    /// <summary>Set when the row is dead-lettered after the final attempt.</summary>
    public string? DeadLetterReason { get; set; }
}
