namespace Stockpile.Domain.Common;

/// <summary>
/// A raised domain event, translated to an <c>OutboxMessage</c> row inside the
/// same transaction as the aggregate change (architecture §10.1, domain-model §15).
/// Payload is serialised JSON written by the outbox interceptor, never by the
/// raising code.
/// </summary>
public sealed record DomainEvent(
    DateTime OccurredAtUtc,
    Guid? ActorId,
    string? CorrelationId,
    string EventType,
    string Payload);
