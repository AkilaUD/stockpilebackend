using Microsoft.EntityFrameworkCore;
using Stockpile.Application.Common.Errors;
using Stockpile.Application.Orders;
using Stockpile.Contracts.Errors;
using Stockpile.Domain.Entities.Catalogue;
using Stockpile.Domain.Entities.Orders;
using Stockpile.Domain.Entities.Procurement;
using Stockpile.Domain.Enums;

namespace Stockpile.Infrastructure.Persistence;

/// <summary>
/// EF Core implementation of the order port (architecture §8.5 module "orders").
/// The acceptance write is a single <c>SaveChanges</c>: quote, RFQ, order, lines,
/// stock holds, both status ledgers and the outbox rows commit together or not at
/// all, and the two integrity backstops — the quote's rowversion and the unique
/// <c>PurchaseOrders.QuoteId</c> index — turn a concurrent second accept into
/// <c>QUOTE_ALREADY_ACCEPTED</c> instead of a second order.
/// </summary>
public sealed class OrdersStore(StockpileDbContext db) : IOrdersStore
{
    /// <inheritdoc />
    public Task<bool> OrderNumberExistsAsync(string orderNumber, CancellationToken cancellationToken)
        => db.PurchaseOrders.AnyAsync(order => order.OrderNumber == orderNumber, cancellationToken);

    /// <inheritdoc />
    public async Task<QuoteAcceptance?> LoadAcceptanceAsync(
        IReadOnlyCollection<Guid> organizationIds, Guid quoteId, CancellationToken cancellationToken)
    {
        // Ownership in the predicate: a quote against another tenant's RFQ matches no row.
        Quote? quote = await db.Quotes
            .Where(candidate => candidate.PublicId == quoteId
                && candidate.Status != QuoteStatus.Draft
                && db.Rfqs.Any(rfq => rfq.PublicId == candidate.RfqId && organizationIds.Contains(rfq.OrganizationId)))
            .FirstOrDefaultAsync(cancellationToken);
        if (quote is null)
        {
            return null;
        }

        Rfq rfq = await db.Rfqs.FirstAsync(candidate => candidate.PublicId == quote.RfqId, cancellationToken);

        QuoteRevision revision = await db.QuoteRevisions
            .AsNoTracking()
            .FirstAsync(
                candidate => candidate.QuoteId == quote.PublicId && candidate.RevisionNumber == quote.CurrentRevisionNumber,
                cancellationToken);
        List<QuoteRevisionItem> items = await db.QuoteRevisionItems
            .AsNoTracking()
            .Where(item => item.RevisionId == revision.PublicId)
            .ToListAsync(cancellationToken);

        Dictionary<Guid, int> lineNumbers = await db.RfqItems
            .AsNoTracking()
            .Where(item => item.RfqId == rfq.PublicId)
            .ToDictionaryAsync(item => item.PublicId, item => item.LineNumber, cancellationToken);

        // Stock is held only for LIVE-TRACKED stock of the QUOTING supplier's own
        // products: a quote line naming someone else's product, or a migrated
        // untracked line (domain-model §4.5), never touches a stock row.
        List<Guid> productIds = items
            .Where(item => item.ProductId.HasValue)
            .Select(item => item.ProductId!.Value)
            .Distinct()
            .ToList();
        Dictionary<Guid, ProductStock> trackedStock = [];
        if (productIds.Count > 0)
        {
            List<Guid> ownProductIds = await db.Products
                .AsNoTracking()
                .Where(product => productIds.Contains(product.PublicId) && product.SupplierId == quote.SupplierId)
                .Select(product => product.PublicId)
                .ToListAsync(cancellationToken);

            trackedStock = await db.ProductStocks
                .Where(stock => ownProductIds.Contains(stock.ProductId) && stock.IsStockTracked)
                .ToDictionaryAsync(stock => stock.ProductId, cancellationToken);
        }

        Guid? existingOrderId = await db.PurchaseOrders
            .AsNoTracking()
            .Where(order => order.QuoteId == quote.PublicId)
            .Select(order => (Guid?)order.PublicId)
            .FirstOrDefaultAsync(cancellationToken);

        return new QuoteAcceptance(quote, rfq, revision, items, lineNumbers, trackedStock, existingOrderId);
    }

    /// <inheritdoc />
    public async Task AddOrderAsync(
        PurchaseOrder order,
        IReadOnlyCollection<PurchaseOrderItem> items,
        IReadOnlyCollection<PurchaseOrderStatusHistory> history,
        IReadOnlyCollection<RfqStatusHistory> rfqHistory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(rfqHistory);

        db.PurchaseOrders.Add(order);
        db.PurchaseOrderItems.AddRange(items);
        db.PurchaseOrderStatusHistories.AddRange(history);
        db.RfqStatusHistories.AddRange(rfqHistory);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            // A stock row moving under us is a different story from a rival accept.
            throw exception.Entries.Any(entry => entry.Entity is ProductStock)
                ? new AppException(ErrorCodes.StockChanged)
                : new AppException(ErrorCodes.QuoteAlreadyAccepted);
        }
        catch (DbUpdateException exception) when (SqlErrors.IsUniqueViolation(exception))
        {
            // One order per quote (unique QuoteId): the rival accept got there first.
            throw new AppException(ErrorCodes.QuoteAlreadyAccepted);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<OrderRecord>> ListForBuyerAsync(
        IReadOnlyCollection<Guid> organizationIds, int limit, CancellationToken cancellationToken)
    {
        List<PurchaseOrder> found = await db.PurchaseOrders
            .AsNoTracking()
            .Where(order => organizationIds.Contains(order.BuyerOrganizationId))
            .OrderByDescending(order => order.CreatedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);
        return await ToRecordsAsync(found, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<OrderRecord>> ListForSupplierAsync(
        Guid supplierId, int limit, CancellationToken cancellationToken)
    {
        List<PurchaseOrder> found = await db.PurchaseOrders
            .AsNoTracking()
            .Where(order => order.SupplierId == supplierId)
            .OrderByDescending(order => order.CreatedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);
        return await ToRecordsAsync(found, cancellationToken);
    }

    /// <inheritdoc />
    public Task<OrderDetail?> LoadForBuyerAsync(
        IReadOnlyCollection<Guid> organizationIds, Guid orderId, bool forUpdate, CancellationToken cancellationToken)
        => LoadOneAsync(
            db.PurchaseOrders.Where(order =>
                order.PublicId == orderId && organizationIds.Contains(order.BuyerOrganizationId)),
            forUpdate,
            cancellationToken);

    /// <inheritdoc />
    public Task<OrderDetail?> LoadForSupplierAsync(
        Guid supplierId, Guid orderId, bool forUpdate, CancellationToken cancellationToken)
        => LoadOneAsync(
            db.PurchaseOrders.Where(order => order.PublicId == orderId && order.SupplierId == supplierId),
            forUpdate,
            cancellationToken);

    /// <inheritdoc />
    public async Task SaveTransitionAsync(
        PurchaseOrder order, PurchaseOrderStatusHistory entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(entry);

        db.PurchaseOrderStatusHistories.Add(entry);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new AppException(ErrorCodes.ConcurrencyConflict);
        }
    }

    private async Task<OrderDetail?> LoadOneAsync(
        IQueryable<PurchaseOrder> query, bool forUpdate, CancellationToken cancellationToken)
    {
        PurchaseOrder? order = await (forUpdate ? query : query.AsNoTracking()).FirstOrDefaultAsync(cancellationToken);
        if (order is null)
        {
            return null;
        }

        OrderRecord record = (await ToRecordsAsync([order], cancellationToken)).Single();
        List<PurchaseOrderItem> items = await db.PurchaseOrderItems
            .AsNoTracking()
            .Where(item => item.PurchaseOrderId == order.PublicId)
            .OrderBy(item => item.LineNumber)
            .ToListAsync(cancellationToken);
        List<PurchaseOrderStatusHistory> history = await db.PurchaseOrderStatusHistories
            .AsNoTracking()
            .Where(entry => entry.PurchaseOrderId == order.PublicId)
            .OrderBy(entry => entry.OccurredAt)
            .ToListAsync(cancellationToken);

        return new OrderDetail(record, items, history);
    }

    /// <summary>Decorates orders with the display names a list needs in set-based queries (never one per row).</summary>
    private async Task<List<OrderRecord>> ToRecordsAsync(
        List<PurchaseOrder> found, CancellationToken cancellationToken)
    {
        if (found.Count == 0)
        {
            return [];
        }

        List<Guid> supplierIds = found.Select(order => order.SupplierId).Distinct().ToList();
        List<Guid> organizationIds = found.Select(order => order.BuyerOrganizationId).Distinct().ToList();
        List<Guid> quoteIds = found.Select(order => order.QuoteId).Distinct().ToList();
        List<Guid> revisionIds = found.Select(order => order.QuoteRevisionId).Distinct().ToList();

        Dictionary<Guid, string> supplierNames = await db.Suppliers
            .AsNoTracking()
            .Where(supplier => supplierIds.Contains(supplier.PublicId))
            .ToDictionaryAsync(supplier => supplier.PublicId, supplier => supplier.DisplayName, cancellationToken);
        Dictionary<Guid, string> buyerNames = await db.Organizations
            .AsNoTracking()
            .Where(organization => organizationIds.Contains(organization.PublicId))
            .ToDictionaryAsync(organization => organization.PublicId, organization => organization.Name, cancellationToken);
        Dictionary<Guid, string> quoteNumbers = await db.Quotes
            .AsNoTracking()
            .Where(quote => quoteIds.Contains(quote.PublicId))
            .ToDictionaryAsync(quote => quote.PublicId, quote => quote.QuoteNumber, cancellationToken);
        Dictionary<Guid, int> revisionNumbers = await db.QuoteRevisions
            .AsNoTracking()
            .Where(revision => revisionIds.Contains(revision.PublicId))
            .ToDictionaryAsync(revision => revision.PublicId, revision => revision.RevisionNumber, cancellationToken);

        return found.Select(order => new OrderRecord(
            order,
            supplierNames.GetValueOrDefault(order.SupplierId, string.Empty),
            buyerNames.GetValueOrDefault(order.BuyerOrganizationId, string.Empty),
            quoteNumbers.GetValueOrDefault(order.QuoteId, string.Empty),
            revisionNumbers.GetValueOrDefault(order.QuoteRevisionId))).ToList();
    }
}
