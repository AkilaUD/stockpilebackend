using Stockpile.Domain.Enums;

namespace Stockpile.Application.Audit;

/// <summary>
/// One append-only accountability fact (domain-model §10, security-test-matrix
/// X9). Actor, client address, user agent and correlation id ride the ambient
/// request; only the action's own facts are explicit here. Metadata is
/// structured JSON context written by the CALLER — it must never carry a
/// password, token, hash or reset secret (Z4).
/// </summary>
public sealed record AuditEntry(
    string Action,
    string TargetType,
    string? TargetId,
    AuditResult Result,
    string? Metadata = null);

/// <summary>
/// Writes <see cref="AuditLog"/> rows into the application database. Rows are
/// append-only (no update or delete path — X9) and are committed atomically
/// with the change they describe whenever the caller is mid-transaction.
/// </summary>
public interface IAuditService
{
    Task RecordAsync(AuditEntry entry, CancellationToken cancellationToken = default);
}
