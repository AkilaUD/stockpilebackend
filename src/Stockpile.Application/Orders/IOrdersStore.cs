using Stockpile.Domain.Entities.Catalogue;
using Stockpile.Domain.Entities.Orders;
using Stockpile.Domain.Entities.Procurement;

namespace Stockpile.Application.Orders;

/// <summary>
/// Order data port (architecture §8.5 module "orders"). The accept flow is ONE
/// unit of work: the store loads the quote, its RFQ and the tracked stock rows the
/// order will hold, the use case mutates them, and <see cref="AddOrderAsync"/>
/// commits quote + RFQ + order + stock + history + outbox in a single
/// <c>SaveChanges</c> — or throws, leaving nothing behind.
/// </summary>
public interface IOrdersStore
{
    /// <summary>True when an order already carries this display reference (unique).</summary>
    Task<bool> OrderNumberExistsAsync(string orderNumber, CancellationToken cancellationToken);

    /// <summary>
    /// Everything accepting a quote needs, resolved through the caller's buyer
    /// organisations (another tenant's quote id is null — BOLA, api.md §1.6). The
    /// quote, the RFQ and the stock rows are returned TRACKED so the one save
    /// persists their changes together with the order.
    /// </summary>
    Task<QuoteAcceptance?> LoadAcceptanceAsync(
        IReadOnlyCollection<Guid> organizationIds, Guid quoteId, CancellationToken cancellationToken);

    /// <summary>
    /// Commits the acceptance in one <c>SaveChanges</c>. A lost race (the quote's
    /// rowversion moved, or another order already claimed the quote) surfaces as
    /// <c>QUOTE_ALREADY_ACCEPTED</c>; a lost race on a stock row as
    /// <c>STOCK_CHANGED</c>. Nothing is half-written either way.
    /// </summary>
    Task AddOrderAsync(
        PurchaseOrder order,
        IReadOnlyCollection<PurchaseOrderItem> items,
        IReadOnlyCollection<PurchaseOrderStatusHistory> history,
        IReadOnlyCollection<RfqStatusHistory> rfqHistory,
        CancellationToken cancellationToken);

    /// <summary>The buyer organisations' orders, newest first, capped at <paramref name="limit"/>.</summary>
    Task<IReadOnlyList<OrderRecord>> ListForBuyerAsync(
        IReadOnlyCollection<Guid> organizationIds, int limit, CancellationToken cancellationToken);

    /// <summary>The supplier's orders, newest first, capped at <paramref name="limit"/>.</summary>
    Task<IReadOnlyList<OrderRecord>> ListForSupplierAsync(
        Guid supplierId, int limit, CancellationToken cancellationToken);

    /// <summary>One order of the buyer organisations; any other id is null (BOLA).</summary>
    Task<OrderDetail?> LoadForBuyerAsync(
        IReadOnlyCollection<Guid> organizationIds, Guid orderId, bool forUpdate, CancellationToken cancellationToken);

    /// <summary>One order of the supplier; any other id is null (BOLA).</summary>
    Task<OrderDetail?> LoadForSupplierAsync(
        Guid supplierId, Guid orderId, bool forUpdate, CancellationToken cancellationToken);

    /// <summary>Appends a status-history row and persists the order's own transition together; a lost race is <c>CONCURRENCY_CONFLICT</c>.</summary>
    Task SaveTransitionAsync(PurchaseOrder order, PurchaseOrderStatusHistory entry, CancellationToken cancellationToken);
}

/// <summary>The tracked facts of one quote acceptance (see <see cref="IOrdersStore.LoadAcceptanceAsync"/>).</summary>
/// <param name="Quote">Tracked.</param>
/// <param name="Rfq">Tracked.</param>
/// <param name="Revision">The current, immutable revision being accepted.</param>
/// <param name="Items">The revision's lines.</param>
/// <param name="LineNumbers">RFQ line number per RFQ item id, for a stable line order.</param>
/// <param name="TrackedStock">Tracked stock rows by product id — only live-tracked stock of the QUOTING supplier's own products.</param>
/// <param name="ExistingOrderId">The order already created from this quote, when there is one.</param>
public sealed record QuoteAcceptance(
    Quote Quote,
    Rfq Rfq,
    QuoteRevision Revision,
    IReadOnlyList<QuoteRevisionItem> Items,
    IReadOnlyDictionary<Guid, int> LineNumbers,
    IReadOnlyDictionary<Guid, ProductStock> TrackedStock,
    Guid? ExistingOrderId);

/// <summary>An order with the display identities a list needs.</summary>
public sealed record OrderRecord(
    PurchaseOrder Order,
    string SupplierName,
    string BuyerName,
    string QuoteNumber,
    int QuoteRevisionNumber);

/// <summary>An order with its lines and status ledger.</summary>
public sealed record OrderDetail(
    OrderRecord Record,
    IReadOnlyList<PurchaseOrderItem> Items,
    IReadOnlyList<PurchaseOrderStatusHistory> History);
