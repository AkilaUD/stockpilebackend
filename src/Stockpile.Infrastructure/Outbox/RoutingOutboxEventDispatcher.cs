using Microsoft.Extensions.Logging;
using Stockpile.Application.Outbox;

namespace Stockpile.Infrastructure.Outbox;

/// <summary>
/// Routes each claimed row to every consumer registered for its event type
/// (architecture §10.1, §10.2 catalogue). A type with no consumer yet is
/// treated as delivered: the catalogue's consumers arrive with their owning
/// phases, and a pipeline that wedges on a not-yet-implemented consumer would
/// dead-letter legitimate rows by backoff alone. The WARNING log line is the
/// ops-visible marker until that phase lands.
/// </summary>
public sealed class RoutingOutboxEventDispatcher(
    IEnumerable<IOutboxConsumer> handlers,
    ILogger<RoutingOutboxEventDispatcher> logger) : IOutboxEventDispatcher
{
    /// <inheritdoc />
    public async Task DispatchAsync(OutboxDelivery delivery, CancellationToken cancellationToken = default)
    {
        IOutboxConsumer[] consumers = handlers
            .Where(consumer => consumer.Supports(delivery.EventType))
            .ToArray();

        if (consumers.Length == 0)
        {
            OutboxLog.NoConsumer(logger, delivery.EventType);
            return;
        }

        foreach (IOutboxConsumer consumer in consumers)
        {
            await consumer.HandleAsync(delivery, cancellationToken);
        }
    }
}
