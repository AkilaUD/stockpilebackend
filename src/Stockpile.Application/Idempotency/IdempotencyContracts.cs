namespace Stockpile.Application.Idempotency;

/// <summary>How a critical write (api.md §1.7, spec §14) ended up.</summary>
public enum IdempotencyResultKind
{
    /// <summary>The operation ran once and its response artifacts were stored.</summary>
    Processed,

    /// <summary>A previous request with the same key and request hash already
    /// ran; its stored response must be replayed unchanged.</summary>
    Replayed,

    /// <summary>This key was already used with a different request — the caller
    /// must answer <c>409 IDEMPOTENCY_KEY_REUSE</c> without running the action.</summary>
    KeyConflict,
}

/// <summary>
/// Store outcome for one <see cref="IIdempotencyStore.ExecuteAsync"/> call.
/// Only the success reference and status are carried — the "replay-safe"
/// contract (architecture §9.4) replays the stored response reference, not the
/// full original payload, per the <c>nvarchar(400)</c> reference column.
/// </summary>
public sealed record IdempotencyResult(
    IdempotencyResultKind Kind,
    int? Status = null,
    string? Reference = null);
