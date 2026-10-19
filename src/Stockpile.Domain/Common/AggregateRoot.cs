using System.Text.Json;

namespace Stockpile.Domain.Common;

/// <summary>
/// Audited aggregate root that records <see cref="DomainEvent"/>s while a use
/// case runs (domain-model §15). The outbox interceptor is the only reader: at
/// <c>SaveChanges</c> it turns <see cref="Events"/> into <c>OutboxMessage</c>
/// rows inside the same transaction as the aggregate change (architecture
/// §10.1), then calls <see cref="ClearEvents"/>.
/// <para>
/// Payloads are ids and non-secret facts only — never a token, hash, address
/// line or free-text note (Z4).
/// </para>
/// </summary>
public abstract class AggregateRoot : AuditableEntity, IHasDomainEvents
{
    private static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web);

    private readonly List<DomainEvent> _events = [];

    /// <inheritdoc />
    public IReadOnlyCollection<DomainEvent> Events => _events;

    /// <inheritdoc />
    public void ClearEvents() => _events.Clear();

    /// <summary>Records one event (domain-model §15 catalogue name) with its JSON payload.</summary>
    public void Raise(string eventType, Guid? actorId, string? correlationId, object payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        ArgumentNullException.ThrowIfNull(payload);

        _events.Add(new DomainEvent(
            DateTime.UtcNow,
            actorId,
            correlationId,
            eventType,
            JsonSerializer.Serialize(payload, PayloadOptions)));
    }
}
