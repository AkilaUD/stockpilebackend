using Stockpile.Domain.Enums;

namespace Stockpile.Application.Audit;

/// <summary>
/// Appends a <see cref="SecurityEvent"/> row (domain-model §10) — the
/// tiered-by-retention (2-year) security record, separate from the 7-year
/// <c>AuditLog</c>. <paramref name="detail"/> must be a short human phrase and
/// <paramref name="metadata"/> structured JSON — NEVER a password, token,
/// hash, or reset secret (Z4; the <c>Detail</c> column is nvarchar(500)).
/// </summary>
public interface ISecurityEventService
{
    Task RecordAsync(
        SecurityEventType type,
        Guid? userId = null,
        string? detail = null,
        string? metadata = null,
        CancellationToken cancellationToken = default);
}
