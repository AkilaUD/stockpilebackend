namespace Stockpile.Application.Idempotency;

/// <summary>
/// Replay guard for critical writes (architecture §9.4, spec §14). One call
/// guarantees the operation ran <em>at most once</em> per (owner, key): identical
/// replays return the stored response, a different request on the same key is
/// a conflict, and a fresh key executes the payload with the key row committed
/// in the same database transaction — a crashed request leaves nothing behind
/// and is safe to retry.
/// </summary>
public interface IIdempotencyStore
{
    /// <summary>
    /// Runs <paramref name="execute"/> at most once per
    /// (<paramref name="ownerId"/>, <paramref name="key"/>).
    /// </summary>
    /// <param name="ownerId">Key scope — the acting user's GUID, or a stable
    /// per-caller identity for anonymous (webhook-style) traffic.</param>
    /// <param name="key">Client-supplied <c>Idempotency-Key</c> token.</param>
    /// <param name="requestHash">SHA-256 of method + path + raw body; a match
    /// replays, a mismatch is a conflict.</param>
    /// <param name="execute">The payload to run. Returns the observed HTTP
    /// status and the short response reference to store for replays.
    /// Implementations invoke it inside the key row's transaction, so the
    /// payload's own writes (same scoped DbContext) commit atomically with the
    /// key. A thrown <paramref name="execute"/> rolls everything back.</param>
    Task<IdempotencyResult> ExecuteAsync(
        Guid ownerId,
        string key,
        string requestHash,
        Func<CancellationToken, Task<(int Status, string? Reference)>> execute,
        CancellationToken cancellationToken = default);
}
