using Microsoft.EntityFrameworkCore;
using Stockpile.Application.Procurement;
using Stockpile.Domain.Entities.Catalogue;
using Stockpile.Domain.Entities.Procurement;

namespace Stockpile.Infrastructure.Persistence;

/// <summary>
/// EF Core implementation of the quote port (architecture §8.5 module
/// "procurement"). The submission read composes everything the use case needs —
/// the lead, its RFQ, the RFQ's lines and the catalogue names those lines must be
/// snapshotted from — so the Application layer never re-joins reference data, and
/// the write is a single <c>SaveChanges</c> so the quote, its first revision, its
/// lines and the lead's own transition commit together or not at all.
/// </summary>
public sealed class QuotesStore(StockpileDbContext db) : IQuotesStore
{
    /// <inheritdoc />
    public Task<bool> QuoteNumberExistsAsync(string quoteNumber, CancellationToken cancellationToken)
        => db.Quotes.AnyAsync(quote => quote.QuoteNumber == quoteNumber, cancellationToken);

    /// <inheritdoc />
    public async Task<QuoteSubmission?> LoadSubmissionAsync(
        Guid supplierId, Guid rfqMatchId, CancellationToken cancellationToken)
    {
        // Scoped by supplier FIRST: a foreign lead matches no row at all, so the
        // caller cannot tell "not yours" from "does not exist" (BOLA, api.md §1.6).
        RfqMatch? match = await db.RfqMatches
            .FirstOrDefaultAsync(
                candidate => candidate.PublicId == rfqMatchId && candidate.SupplierId == supplierId,
                cancellationToken);
        if (match is null)
        {
            return null;
        }

        Rfq? rfq = await db.Rfqs.FirstOrDefaultAsync(
            candidate => candidate.PublicId == match.RfqId, cancellationToken);
        if (rfq is null)
        {
            return null;
        }

        List<RfqItem> items = await db.RfqItems
            .AsNoTracking()
            .Where(item => item.RfqId == rfq.PublicId)
            .OrderBy(item => item.LineNumber)
            .ToListAsync(cancellationToken);

        // The catalogue name/SKU a quoted line snapshots (domain-model §6.2). Lines
        // without a product keep the buyer's own requirement text as the name.
        List<Guid> productIds = items
            .Where(item => item.ProductId.HasValue)
            .Select(item => item.ProductId!.Value)
            .Distinct()
            .ToList();
        Dictionary<Guid, Product> products = productIds.Count == 0
            ? new Dictionary<Guid, Product>()
            : await db.Products
                .AsNoTracking()
                .Where(product => productIds.Contains(product.PublicId))
                .ToDictionaryAsync(product => product.PublicId, cancellationToken);

        Quote? existing = await db.Quotes
            .AsNoTracking()
            .FirstOrDefaultAsync(
                quote => quote.RfqId == rfq.PublicId && quote.SupplierId == supplierId,
                cancellationToken);

        return new QuoteSubmission(
            match,
            rfq,
            items.Select(item =>
            {
                products.TryGetValue(item.ProductId ?? Guid.Empty, out Product? product);
                return new QuoteLineContext(item, product?.Name, product?.Sku);
            }).ToList(),
            existing);
    }

    /// <inheritdoc />
    public async Task AddSubmissionAsync(
        Quote quote,
        QuoteRevision revision,
        IReadOnlyCollection<QuoteRevisionItem> items,
        RfqMatch match,
        CancellationToken cancellationToken)
    {
        db.Quotes.Add(quote);
        db.QuoteRevisions.Add(revision);
        db.QuoteRevisionItems.AddRange(items);

        // The lead was loaded tracking-enabled by LoadSubmissionAsync in this same
        // scope, so its status/response stamps ride this save; Update is a no-op
        // guard when it is already tracked.
        db.RfqMatches.Update(match);

        await db.SaveChangesAsync(cancellationToken);
    }
}
