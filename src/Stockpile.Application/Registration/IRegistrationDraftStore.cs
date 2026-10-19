using Stockpile.Domain.Entities.Identity;

namespace Stockpile.Application.Registration;

/// <summary>
/// The registration-draft ledger (business-flows §2/§3, api.md §2.1). The same
/// discipline as the reset/verification ledgers: only the token's SHA-256 hash is
/// stored, so lookups key on the hash alone — the token identifies its draft. A
/// draft is single-use in the sense that completing the registration it described
/// retires it (<see cref="CompleteAsync"/>), after which it can never be resumed.
/// </summary>
public interface IRegistrationDraftStore
{
    /// <summary>Persists a freshly minted draft with its category rows.</summary>
    Task CreateAsync(RegistrationDraft draft, CancellationToken cancellationToken);

    /// <summary>
    /// The live draft for the given hash, or null when the hash is unknown, already
    /// retired, or past <paramref name="nowUtc"/> — the caller answers one identical
    /// 404 for all of them (no reason leaks). Category rows are loaded with it.
    /// </summary>
    Task<RegistrationDraft?> FindLiveByHashAsync(
        string tokenHash, DateTime nowUtc, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces the draft's category rows with <paramref name="categorySlugs"/> and
    /// flushes the scalar prefill the caller has already set on the tracked row.
    /// </summary>
    Task UpdateAsync(
        RegistrationDraft draft, IReadOnlyList<string> categorySlugs, CancellationToken cancellationToken);

    /// <summary>
    /// Retires the draft a completed registration came from: 1 when THIS call won
    /// the race, 0 when the token was already dead (single-use completion).
    /// </summary>
    Task<int> CompleteAsync(
        string tokenHash, Guid userId, DateTime completedAtUtc, CancellationToken cancellationToken);

    /// <summary>
    /// Removes drafts that expired at or before <paramref name="nowUtc"/> — the
    /// sweep that keeps an anonymous flow from growing rows forever, run
    /// opportunistically when a new draft is minted.
    /// </summary>
    Task<int> DeleteExpiredAsync(DateTime nowUtc, CancellationToken cancellationToken);
}
