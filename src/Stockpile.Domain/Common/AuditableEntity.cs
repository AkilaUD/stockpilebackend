namespace Stockpile.Domain.Common;

/// <summary>
/// Audit columns for aggregate roots only — never child rows (domain-model §1).
/// Actor columns hold the acting user's public GUID and are deliberately loose
/// references (no FK): account deletion anonymises the user but must never be
/// blocked by, or cascade into, an audit trail.
/// </summary>
public abstract class AuditableEntity
{
    /// <summary>First write time, UTC (datetime2(7)).</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Creating actor's public GUID; null = system.</summary>
    public Guid? CreatedBy { get; set; }

    /// <summary>Last write time, UTC; null until first update.</summary>
    public DateTime? UpdatedAt { get; set; }

    /// <summary>Last acting actor's public GUID; null = system.</summary>
    public Guid? UpdatedBy { get; set; }
}
