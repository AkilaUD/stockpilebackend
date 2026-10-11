namespace Stockpile.Application.Outbox;

/// <summary>
/// One <c>OutboxMessage</c> handed to the dispatch pipeline (architecture
/// §10.1). The payload is opaque JSON to handlers; only the Worker routes it.
/// </summary>
public sealed record OutboxDelivery(string EventType, string PayloadJson);

/// <summary>
/// Delivery port for claimed outbox rows. Implementations route to the
/// consumers registered for the event type (§10.2 catalogue) and treat a type
/// with no consumer yet as delivered (the catalogue's consumers land with their
/// own phases; a pipeline can never wedge on a not-yet-implemented consumer).
/// Implementations throw only on genuine transport/consumer failure, which the
/// Worker treats as a retryable attempt.
/// </summary>
public interface IOutboxEventDispatcher
{
    Task DispatchAsync(OutboxDelivery delivery, CancellationToken cancellationToken = default);
}

/// <summary>
/// A consumer of outbox events, registered by its owning feature phase against
/// the types it handles (e.g. the identity phase registers a consumer for
/// <c>identity.user.registered</c>). Consumers run INSIDE the Worker's claim
/// transaction, so a consumer that persists through its scoped
/// <c>StockpileDbContext</c> commits atomically with the <c>Processed</c> mark.
/// Consumers must be idempotent — a batch failure is retried and the consumer
/// may run more than once (at-least-once, architecture §10.1).
/// </summary>
public interface IOutboxConsumer
{
    /// <summary>Whether this consumer accepts <paramref name="eventType"/>.</summary>
    bool Supports(string eventType);

    /// <summary>Causes the side effect for the event. Throw to schedule a retry.</summary>
    Task HandleAsync(OutboxDelivery delivery, CancellationToken cancellationToken = default);
}
