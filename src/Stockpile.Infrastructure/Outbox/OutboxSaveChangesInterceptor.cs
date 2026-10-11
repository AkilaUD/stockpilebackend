using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Stockpile.Domain.Common;
using Stockpile.Domain.Entities.Platform;

namespace Stockpile.Infrastructure.Outbox;

/// <summary>
/// Task 1.28 — EF interceptor that materialises raised domain events as
/// <c>OutboxMessage</c> rows during the SAME <c>SaveChanges</c> that commits
/// the aggregate change (architecture §10.1), so the event is never lost and
/// never delivered for a change that didn't happen.
///
/// Capture happens at <c>SavingChanges</c> on every aggregate whose state is
/// Added/Modified/Deleted; the rows are written by the same save (one
/// transaction). Events are cleared from the aggregate at capture time:
/// * Success → rows committed with the change; a second save cannot duplicate.
/// * Failure → the change itself failed and rolled back, so dropping the events
///   is correct (no phantom events); a retry must be a fresh operation.
/// The interceptor needs no request context and no mutable state; it is
/// registered once as a singleton for both Api and Worker.
/// </summary>
public sealed class OutboxSaveChangesInterceptor : SaveChangesInterceptor
{
    private const int MaxEventTypeLength = 100;

    /// <inheritdoc />
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Capture(eventData.Context!);
        return result;
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Capture(eventData.Context!);
        return new ValueTask<InterceptionResult<int>>(result);
    }

    private static void Capture(DbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Materialise before adding rows — Add touches the change tracker that
        // PendingAggregates is enumerating.
        foreach (IHasDomainEvents aggregate in PendingAggregates(context).ToArray())
        {
            foreach (DomainEvent domainEvent in aggregate.Events)
            {
                OutboxGuard.ValidateEventType(domainEvent.EventType, MaxEventTypeLength);

                context.Set<OutboxMessage>().Add(new OutboxMessage
                {
                    EventType = domainEvent.EventType,
                    Payload = domainEvent.Payload,
                    OccurredAt = domainEvent.OccurredAtUtc,
                    CorrelationId = domainEvent.CorrelationId,
                    NextAttemptAt = domainEvent.OccurredAtUtc,
                });
            }

            aggregate.ClearEvents();
        }
    }

    /// <summary>Aggregates being written by this save — only their events flush.</summary>
    private static IEnumerable<IHasDomainEvents> PendingAggregates(DbContext context) =>
        context.ChangeTracker.Entries()
            .Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Select(entry => entry.Entity)
            .OfType<IHasDomainEvents>();
}
