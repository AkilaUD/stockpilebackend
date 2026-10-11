using Microsoft.EntityFrameworkCore;
using Stockpile.Application.Audit;
using Stockpile.Domain.Entities.Platform;
using Stockpile.Domain.Enums;
using Stockpile.Infrastructure.Persistence;

namespace Stockpile.Infrastructure.Audit;

/// <summary>
/// <see cref="ISecurityEventService"/> over the scoped
/// <see cref="StockpileDbContext"/> — same commit model as the audit service:
/// ride an ambient transaction, else save immediately (unless other business
/// changes are staged, in which case the caller's SaveChanges commits together).
/// Append-only (X9).
/// </summary>
public sealed class SecurityEventService(
    StockpileDbContext dbContext,
    ICurrentRequestContext currentRequest) : ISecurityEventService
{
    /// <inheritdoc />
    public async Task RecordAsync(
        SecurityEventType type,
        Guid? userId = null,
        string? detail = null,
        string? metadata = null,
        CancellationToken cancellationToken = default)
    {
        if (detail is { Length: > 500 })
        {
            throw new ArgumentOutOfRangeException(nameof(detail), "SecurityEvent detail exceeds nvarchar(500).");
        }

        int pendingBefore = PendingChangeCount(dbContext);

        dbContext.SecurityEvents.Add(new SecurityEvent
        {
            Type = type,
            ActorId = currentRequest.ActorId,
            UserId = userId,
            IpAddress = Truncate(currentRequest.IpAddress, 45),
            UserAgent = Truncate(currentRequest.UserAgent, 400),
            CorrelationId = Truncate(currentRequest.CorrelationId, 64),
            Detail = detail,
            Metadata = metadata,
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
