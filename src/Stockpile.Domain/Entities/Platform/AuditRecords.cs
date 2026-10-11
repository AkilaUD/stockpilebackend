using Stockpile.Domain.Enums;

namespace Stockpile.Domain.Entities.Platform;

/// <summary>
/// Append-only accountability record (domain-model §10), written in-transaction
/// with the change it describes. Never deleted; 7-year retention. High-volume
/// table → <c>long</c> identity surrogate key.
/// </summary>
public sealed class AuditLog
{
    public long Id { get; set; }

    /// <summary>Acting user's public GUID; null = system.</summary>
    public Guid? ActorId { get; set; }

    /// <summary>Organisation the actor acted within, when scoped.</summary>
    public Guid? ActorOrganizationId { get; set; }

    /// <summary>Stable dotted action code, e.g. <c>user.register</c>, <c>rfq.submit</c>.</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>Target entity type, e.g. <c>RFQ</c>, <c>User</c>.</summary>
    public string TargetType { get; set; } = string.Empty;

    /// <summary>Target identifier (GUID or reference code), nvarchar(100).</summary>
    public string? TargetId { get; set; }

    public AuditResult Result { get; set; }

    public string? IpAddress { get; set; }

    public string? UserAgent { get; set; }

    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;

    /// <summary>Correlation id of the originating request (architecture §14).</summary>
    public string? CorrelationId { get; set; }

    /// <summary>Structured context as JSON (nvarchar(max)) — never secrets.</summary>
    public string? Metadata { get; set; }
}

/// <summary>
/// Security-relevant event (domain-model §10) — separate table with separate
/// retention (2 years) from <see cref="AuditLog"/>. Append-only, never deleted.
/// </summary>
public sealed class SecurityEvent
{
    public long Id { get; set; }

    public SecurityEventType Type { get; set; }

    /// <summary>Who triggered the event; null = system.</summary>
    public Guid? ActorId { get; set; }

    /// <summary>Whose account is affected (lockout, password change, privilege change).</summary>
    public Guid? UserId { get; set; }

    public string? IpAddress { get; set; }

    public string? UserAgent { get; set; }

    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;

    public string? CorrelationId { get; set; }

    /// <summary>Short human detail, nvarchar(500) — never a token, hash, or password.</summary>
    public string? Detail { get; set; }

    /// <summary>Structured context as JSON (nvarchar(max)).</summary>
    public string? Metadata { get; set; }
}
