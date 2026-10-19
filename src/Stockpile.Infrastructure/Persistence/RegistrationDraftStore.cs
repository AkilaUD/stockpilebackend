using Microsoft.EntityFrameworkCore;
using Stockpile.Application.Registration;
using Stockpile.Domain.Entities.Identity;

namespace Stockpile.Infrastructure.Persistence;

/// <summary>
/// The draft ledger over the same scoped context the registration flow runs in.
/// The anonymous save/resume endpoints probe by hash ALONE — the token identifies
/// its draft — so the hash is uniquely indexed for a single-key probe. Completion
/// is an execute-update so the single-use race is won in SQL, not in memory.
/// </summary>
public sealed class RegistrationDraftStore : IRegistrationDraftStore
{
    private readonly StockpileDbContext _db;

    public RegistrationDraftStore(StockpileDbContext db) => _db = db;

    /// <inheritdoc />
    public async Task CreateAsync(RegistrationDraft draft, CancellationToken cancellationToken)
    {
        _db.RegistrationDrafts.Add(draft);
        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task<RegistrationDraft?> FindLiveByHashAsync(
        string tokenHash, DateTime nowUtc, CancellationToken cancellationToken)
        => _db.RegistrationDrafts
            .Include(draft => draft.Categories)
            .FirstOrDefaultAsync(
                draft => draft.TokenHash == tokenHash
                    && draft.CompletedAtUtc == null
                    && draft.ExpiresAtUtc > nowUtc,
                cancellationToken);

    /// <inheritdoc />
    public async Task UpdateAsync(
        RegistrationDraft draft, IReadOnlyList<string> categorySlugs, CancellationToken cancellationToken)
    {
        // The scalar prefill is already set on this tracked row by the service.
        // Replacing the children on the required collection is what EF deletes the
        // orphans for; no manual row surgery.
        draft.Categories.Clear();
        foreach (string slug in categorySlugs)
        {
            draft.Categories.Add(new RegistrationDraftCategory { DraftId = draft.Id, Slug = slug });
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task<int> CompleteAsync(
        string tokenHash, Guid userId, DateTime completedAtUtc, CancellationToken cancellationToken)
        => _db.RegistrationDrafts
            .Where(draft => draft.TokenHash == tokenHash && draft.CompletedAtUtc == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(draft => draft.CompletedAtUtc, completedAtUtc)
                    .SetProperty(draft => draft.UserId, userId),
                cancellationToken);

    /// <inheritdoc />
    public Task<int> DeleteExpiredAsync(DateTime nowUtc, CancellationToken cancellationToken)
        // The FK cascade removes the category rows with their draft.
        => _db.RegistrationDrafts
            .Where(draft => draft.ExpiresAtUtc <= nowUtc)
            .ExecuteDeleteAsync(cancellationToken);
}
