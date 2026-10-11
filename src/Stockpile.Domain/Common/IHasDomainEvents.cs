namespace Stockpile.Domain.Common;

/// <summary>
/// Marks an aggregate that records <see cref="DomainEvent"/>s while a command
/// runs. The outbox interceptor (1.28) is the ONLY reader: at
/// <c>SaveChanges</c> it flushes <see cref="Events"/> into <c>OutboxMessage</c>
/// rows inside the same transaction as the aggregate change (architecture
/// §10.1 — no lost events, no phantom events), then calls
/// <see cref="ClearEvents"/> so nothing is captured twice.
/// </summary>
public interface IHasDomainEvents
{
    /// <summary>Events raised since the last <c>SaveChanges</c>, in raise order.</summary>
    IReadOnlyCollection<DomainEvent> Events { get; }

    /// <summary>Drops the pending collection after the interceptor materialised the rows.</summary>
    void ClearEvents();
}
