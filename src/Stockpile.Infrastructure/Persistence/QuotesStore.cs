using Microsoft.EntityFrameworkCore;
using Stockpile.Application.Common.Errors;
using Stockpile.Application.Procurement;
using Stockpile.Contracts.Errors;
using Stockpile.Domain.Entities.Catalogue;
using Stockpile.Domain.Entities.Procurement;
using Stockpile.Domain.Enums;

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

    // ---- Lifecycle (P2) -------------------------------------------------------

    /// <inheritdoc />
    public Task<QuoteDetail?> LoadForSupplierAsync(
        Guid supplierId, Guid quoteId, bool forUpdate, CancellationToken cancellationToken)
        // Scoped by supplier in the predicate itself: a foreign quote matches no row.
        => LoadOneAsync(
            db.Quotes.Where(quote => quote.PublicId == quoteId && quote.SupplierId == supplierId),
            forUpdate,
            cancellationToken);

    /// <inheritdoc />
    public Task<QuoteDetail?> LoadForBuyerAsync(
        IReadOnlyCollection<Guid> organizationIds, Guid quoteId, bool forUpdate, CancellationToken cancellationToken)
        => LoadOneAsync(
            BuyerQuotes(organizationIds).Where(quote => quote.PublicId == quoteId),
            forUpdate,
            cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<QuoteDetail>> ListForSupplierAsync(
        Guid supplierId, int limit, CancellationToken cancellationToken)
    {
        List<Quote> quotes = await db.Quotes
            .AsNoTracking()
            .Where(quote => quote.SupplierId == supplierId)
            .OrderByDescending(quote => quote.CreatedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);
        return await ComposeAsync(quotes, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<QuoteDetail>> ListForBuyerAsync(
        IReadOnlyCollection<Guid> organizationIds, int limit, CancellationToken cancellationToken)
    {
        List<Quote> quotes = await BuyerQuotes(organizationIds)
            .AsNoTracking()
            .OrderByDescending(quote => quote.CreatedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);
        return await ComposeAsync(quotes, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<RfqQuotes?> LoadRfqQuotesForBuyerAsync(
        IReadOnlyCollection<Guid> organizationIds, Guid rfqId, CancellationToken cancellationToken)
    {
        // Ownership first: an RFQ outside the caller's organisations is null, the same as a missing one.
        Rfq? rfq = await db.Rfqs
            .AsNoTracking()
            .FirstOrDefaultAsync(
                candidate => candidate.PublicId == rfqId && organizationIds.Contains(candidate.OrganizationId),
                cancellationToken);
        if (rfq is null)
        {
            return null;
        }

        List<Quote> quotes = await db.Quotes
            .AsNoTracking()
            .Where(quote => quote.RfqId == rfqId && quote.Status != QuoteStatus.Draft)
            .OrderBy(quote => quote.CreatedAt)
            .ToListAsync(cancellationToken);

        IReadOnlyList<QuoteDetail> details = await ComposeAsync(quotes, cancellationToken);
        Dictionary<Guid, List<QuoteLineContext>> lines = await LoadLinesAsync([rfqId], cancellationToken);
        return new RfqQuotes(rfq, lines.GetValueOrDefault(rfqId) ?? [], details);
    }

    /// <inheritdoc />
    public async Task AddRevisionAsync(
        Quote quote,
        QuoteRevision revision,
        IReadOnlyCollection<QuoteRevisionItem> items,
        NegotiationMessage? reply,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(quote);
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(items);

        db.QuoteRevisions.Add(revision);
        db.QuoteRevisionItems.AddRange(items);
        if (reply is not null)
        {
            db.NegotiationMessages.Add(reply);
        }

        await SaveGuardedAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task AddMessageAsync(Quote quote, NegotiationMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(quote);
        ArgumentNullException.ThrowIfNull(message);

        db.NegotiationMessages.Add(message);
        await SaveGuardedAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task SaveAsync(CancellationToken cancellationToken) => SaveGuardedAsync(cancellationToken);

    /// <summary>
    /// Saves, translating a lost optimistic-concurrency race (the quote's rowversion
    /// moved, or a second revision claimed the same number) into the documented
    /// <c>CONCURRENCY_CONFLICT</c> instead of a 500.
    /// </summary>
    private async Task SaveGuardedAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new AppException(ErrorCodes.ConcurrencyConflict);
        }
        catch (DbUpdateException exception) when (SqlErrors.IsUniqueViolationOn(exception, "QuoteRevisions"))
        {
            throw new AppException(ErrorCodes.ConcurrencyConflict);
        }
    }

    /// <summary>Quotes against RFQs owned by one of the organisations, excluding never-submitted drafts.</summary>
    private IQueryable<Quote> BuyerQuotes(IReadOnlyCollection<Guid> organizationIds)
        => db.Quotes.Where(quote =>
            quote.Status != QuoteStatus.Draft
            && db.Rfqs.Any(rfq => rfq.PublicId == quote.RfqId && organizationIds.Contains(rfq.OrganizationId)));

    private async Task<QuoteDetail?> LoadOneAsync(
        IQueryable<Quote> query, bool forUpdate, CancellationToken cancellationToken)
    {
        Quote? quote = await (forUpdate ? query : query.AsNoTracking()).FirstOrDefaultAsync(cancellationToken);
        if (quote is null)
        {
            return null;
        }

        return (await ComposeAsync([quote], cancellationToken)).Single();
    }

    /// <summary>
    /// Joins quotes with everything a read needs in a handful of set-based queries
    /// (never one per quote). Only the quote itself may be tracked; every other
    /// entity here is read-only history.
    /// </summary>
    private async Task<List<QuoteDetail>> ComposeAsync(
        List<Quote> quotes, CancellationToken cancellationToken)
    {
        if (quotes.Count == 0)
        {
            return [];
        }

        List<Guid> quoteIds = quotes.Select(quote => quote.PublicId).ToList();
        List<Guid> rfqIds = quotes.Select(quote => quote.RfqId).Distinct().ToList();
        List<Guid> supplierIds = quotes.Select(quote => quote.SupplierId).Distinct().ToList();

        Dictionary<Guid, Rfq> rfqs = await db.Rfqs
            .AsNoTracking()
            .Where(rfq => rfqIds.Contains(rfq.PublicId))
            .ToDictionaryAsync(rfq => rfq.PublicId, cancellationToken);

        Dictionary<Guid, (string Name, string Slug)> suppliers = (await db.Suppliers
                .AsNoTracking()
                .Where(supplier => supplierIds.Contains(supplier.PublicId))
                .Select(supplier => new { supplier.PublicId, supplier.DisplayName, supplier.Slug })
                .ToListAsync(cancellationToken))
            .ToDictionary(row => row.PublicId, row => (row.DisplayName, row.Slug));

        List<QuoteRevision> revisions = await db.QuoteRevisions
            .AsNoTracking()
            .Where(revision => quoteIds.Contains(revision.QuoteId))
            .OrderBy(revision => revision.RevisionNumber)
            .ToListAsync(cancellationToken);
        List<Guid> revisionIds = revisions.Select(revision => revision.PublicId).ToList();
        ILookup<Guid, QuoteRevisionItem> itemsByRevision = (await db.QuoteRevisionItems
                .AsNoTracking()
                .Where(item => revisionIds.Contains(item.RevisionId))
                .ToListAsync(cancellationToken))
            .ToLookup(item => item.RevisionId);

        ILookup<Guid, NegotiationMessage> messagesByQuote = (await db.NegotiationMessages
                .AsNoTracking()
                .Where(message => quoteIds.Contains(message.QuoteId))
                .OrderBy(message => message.CreatedAt)
                .ToListAsync(cancellationToken))
            .ToLookup(message => message.QuoteId);

        Dictionary<Guid, Guid> orders = (await db.PurchaseOrders
                .AsNoTracking()
                .Where(order => quoteIds.Contains(order.QuoteId))
                .Select(order => new { order.QuoteId, order.PublicId })
                .ToListAsync(cancellationToken))
            .ToDictionary(row => row.QuoteId, row => row.PublicId);

        Dictionary<Guid, List<QuoteLineContext>> lines = await LoadLinesAsync(rfqIds, cancellationToken);

        return quotes.Select(quote =>
        {
            (string name, string slug) = suppliers.GetValueOrDefault(quote.SupplierId, (string.Empty, string.Empty));
            return new QuoteDetail(
                quote,
                rfqs[quote.RfqId],
                name,
                slug,
                lines.GetValueOrDefault(quote.RfqId) ?? [],
                revisions
                    .Where(revision => revision.QuoteId == quote.PublicId)
                    .Select(revision => new QuoteRevisionDetail(revision, itemsByRevision[revision.PublicId].ToList()))
                    .ToList(),
                messagesByQuote[quote.PublicId].ToList(),
                orders.TryGetValue(quote.PublicId, out Guid orderId) ? orderId : null);
        }).ToList();
    }

    /// <summary>The RFQs' lines in stable order, each with its catalogue name/SKU snapshot source.</summary>
    private async Task<Dictionary<Guid, List<QuoteLineContext>>> LoadLinesAsync(
        IReadOnlyCollection<Guid> rfqIds, CancellationToken cancellationToken)
    {
        List<RfqItem> items = await db.RfqItems
            .AsNoTracking()
            .Where(item => rfqIds.Contains(item.RfqId))
            .OrderBy(item => item.LineNumber)
            .ToListAsync(cancellationToken);

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

        return items
            .GroupBy(item => item.RfqId)
            .ToDictionary(
                group => group.Key,
                group => group.Select(item =>
                {
                    products.TryGetValue(item.ProductId ?? Guid.Empty, out Product? product);
                    return new QuoteLineContext(item, product?.Name, product?.Sku);
                }).ToList());
    }
}
