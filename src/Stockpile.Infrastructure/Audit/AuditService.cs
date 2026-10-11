using Microsoft.EntityFrameworkCore;
using Stockpile.Application.Audit;
using Stockpile.Domain.Entities.Platform;
using Stockpile.Infrastructure.Persistence;

namespace Stockpile.Infrastructure.Audit;

/// <summary>
/// <see cref="IAuditService"/> over the scoped <see cref="StockpileDbContext"/>.
///
/// Commit model:
/// * Inside an ambient transaction (the idempotency store, a state machine in
///   an explicit transaction) the row rides that transaction's commit —
///   audit is atomic with the change it describes.
/// * Without a transaction, the row is saved immediately UNLESS the context
///   already carries other pending changes (a caller staging its business
///   entities): then the save is left to the caller's own SaveChanges, which
///   commits audit + change together. Saving mid-stage would commit the
///   caller's entities early and break atomicity.
/// Append-only: this service only Adds; nothing in the codebase updates or
/// deletes AuditLog rows (security-test-matrix X9).
/// </summary>
public sealed class AuditService(
    StockpileDbContext dbContext,
    ICurrentRequestContext currentRequest) : IAuditService
{
    /// <inheritdoc />
    public async Task RecordAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        Guard.Require(entry.Action, "Action", 100);
        Guard.Require(entry.TargetType, "TargetType", 100);

        int pendingBefore = PendingChangeCount(dbContext);

        dbContext.AuditLogs.Add(new AuditLog
        {
            ActorId = currentRequest.ActorId,
            Action = entry.Action,
            TargetType = entry.TargetType,
            TargetId = Truncate(entry.TargetId, 100),
            Result = entry.Result,
            IpAddress = Truncate(currentRequest.IpAddress, 45),
            UserAgent = Truncate(currentRequest.UserAgent, 400),
            CorrelationId = Truncate(currentRequest.CorrelationId, 64),
            Metadata = entry.Metadata,
        });

        if (dbContext.Database.CurrentTransaction is null
            && PendingChangeCount(dbContext) == pendingBefore + 1)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    private static int PendingChangeCount(DbContext context) =>
        context.ChangeTracker.Entries().Count(entry =>
            entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted);

    private static string? Truncate(string? value, int maxLength) =>
        value is null || value.Length <= maxLength ? value : value[..maxLength];
}
