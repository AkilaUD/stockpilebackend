namespace Stockpile.Domain.Entities.Platform;

/// <summary>
/// Replay guard for unsafe API calls declaring an <c>Idempotency-Key</c> header
/// (architecture §9.4). Composite natural key; 7-day expiry, then purged.
/// The key row and the stored response commit in one transaction, so a crashed
/// request never leaves a half-written key behind.
/// </summary>
public sealed class IdempotencyKey
{
    /// <summary>Key owner (user's public GUID, or a webhook subject) — scopes keys per caller.</summary>
    public Guid OwnerId { get; set; }

    /// <summary>Client-supplied key, nvarchar(100).</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>SHA-256 of method + path + body; a mismatch on the same key is a 409.</summary>
    public string RequestHash { get; set; } = string.Empty;

    /// <summary>Stored response body reference (identifier/status payload), nvarchar(400)?.</summary>
    public string? ResponseReference { get; set; }

    /// <summary>Stored HTTP status to replay with; null until completed.</summary>
    public int? ResponseStatus { get; set; }

    public DateTime ExpiresAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
