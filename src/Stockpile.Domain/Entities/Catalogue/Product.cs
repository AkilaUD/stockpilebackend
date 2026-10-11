using Stockpile.Domain.Common;
using Stockpile.Domain.Enums;

namespace Stockpile.Domain.Entities.Catalogue;

/// <summary>
/// Aggregate root (domain-model §4.3). A catalogue line sold by a supplier in
/// exactly one category. Price/stock/specs are children here so they version
/// independently of the catalogue description.
/// </summary>
public sealed class Product : AuditableEntity
{
    public Guid PublicId { get; set; } = Guid.CreateVersion7();

    /// <summary>Owning supplier (Supplier aggregate root).</summary>
    public Guid SupplierId { get; set; }

    /// <summary>Assigned category — the leaf-most breadcrumb entry on the live site.</summary>
    public Guid CategoryId { get; set; }

    public ItemType ItemType { get; set; } = ItemType.PhysicalProduct;

    public string Name { get; set; } = string.Empty;

    /// <summary>URL-safe unique slug, e.g. <c>jielian-invisible-waterproof-agent</c>.</summary>
    public string Slug { get; set; } = string.Empty;

    /// <summary>Supplier-scoped stock keeping unit; unique per supplier.</summary>
    public string Sku { get; set; } = string.Empty;

    /// <summary>Brand reference (definition row).</summary>
    public Guid? BrandId { get; set; }

    /// <summary>Manufacturer reference (definition row).</summary>
    public Guid? ManufacturerId { get; set; }

    /// <summary>One-line pitch shown in listings and search results.</summary>
    public string? Summary { get; set; }

    /// <summary>Full description as harvested from the live product page (plain text).</summary>
    public string? Description { get; set; }

    public ProductStatus Status { get; set; } = ProductStatus.Draft;

    /// <summary>Country of origin (ISO alpha-2 code row, domain-model §4.6).</summary>
    public string? CountryOfOriginCode { get; set; }

    /// <summary>Smallest orderable quantity at the public price; null when not declared.</summary>
    public decimal? MinOrderQuantity { get; set; }

    /// <summary>Declared fulfilment lead time in days, when published.</summary>
    public int? LeadTimeDays { get; set; }

    /// <summary>Tax-class reference; reserved for Phase 5 pricing work.</summary>
    public string? TaxClassCode { get; set; }

    /// <summary>When the product was first published (migration date for migrated records).</summary>
    public DateTime? PublishedAt { get; set; }

    /// <summary>Bumped whenever the searchable projection changes (§4.3).</summary>
    public long SearchDocumentVersion { get; set; }

    /// <summary>SQL rowversion for optimistic concurrency on this aggregate root.</summary>
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>
/// One typed attribute value on a product (domain-model §4.2). Exactly one of
/// ValueText / ValueNumber / ValueBool / OptionId carries data, matching the
/// attribute definition's data type.
/// </summary>
public sealed class ProductAttributeValue
{
    public Guid PublicId { get; set; } = Guid.CreateVersion7();

    public Guid ProductId { get; set; }

    public Guid AttributeId { get; set; }

    public string? ValueText { get; set; }

    public decimal? ValueNumber { get; set; }

    public bool? ValueBool { get; set; }

    /// <summary>For Enum/MultiSelect values: the selected AttributeOption public id.</summary>
    public Guid? OptionId { get; set; }
}

/// <summary>
/// A product price point (domain-model §4.4). Money is decimal(18,2) with an
/// explicit ISO currency code — never float.
/// </summary>
public sealed class ProductPrice
{
    public Guid PublicId { get; set; } = Guid.CreateVersion7();

    public Guid ProductId { get; set; }

    /// <summary>ISO 4217 code, e.g. <c>LKR</c>.</summary>
    public string CurrencyCode { get; set; } = "LKR";

    public decimal Amount { get; set; }

    public PriceState PriceState { get; set; } = PriceState.Public;

    /// <summary>Quantity this price point applies from; null = any quantity.</summary>
    public decimal? MinQty { get; set; }

    /// <summary>Quantity this price point applies up to; null = open-ended (typically the top tier).</summary>
    public decimal? MaxQty { get; set; }

    public DateTime EffectiveAt { get; set; } = DateTime.UtcNow;

    /// <summary>Public visibility inherited from the supplier's audience policy (§3.4).</summary>
    public bool IsPublic { get; set; } = true;
}

/// <summary>
/// Append-only ledger of every price change (domain-model §4.4 invariant: price
/// changes never overwrite history). Initial rows are written by the catalogue
/// migration with an explicit actor and reason.
/// </summary>
public sealed class ProductPriceHistory
{
    public Guid PublicId { get; set; } = Guid.CreateVersion7();

    public Guid ProductId { get; set; }

    public string CurrencyCode { get; set; } = "LKR";

    public decimal PreviousAmount { get; set; }

    public decimal NewAmount { get; set; }

    public DateTime ChangedAt { get; set; } = DateTime.UtcNow;

    public Guid? ChangedBy { get; set; }

    public string? Reason { get; set; }
}

/// <summary>
/// Current stock position (domain-model §4.5). Migrated catalogue lines are not
/// live-tracked: the imported OnHand is a documented baseline and stock state is
/// the supplier-declared availability. Invariants: OnHand ≥ 0, Reserved ≥ 0.
/// </summary>
public sealed class ProductStock
{
    public Guid PublicId { get; set; } = Guid.CreateVersion7();

    public Guid ProductId { get; set; }

    /// <summary>False for migrated lines: no live movements feed this figure yet.</summary>
    public bool IsStockTracked { get; set; }

    public StockState StockState { get; set; } = StockState.EnquireForAvailability;

    public int OnHand { get; set; }

    public int Reserved { get; set; }

    public int? LowStockThreshold { get; set; }

    public DateTime? LastMovementAt { get; set; }

    /// <summary>SQL rowversion for optimistic concurrency.</summary>
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>
/// Append-only ledger of stock movements (domain-model §4.5). Initial entries
/// are written by the catalogue migration as a documented baseline and never
/// edited; future warehouse writes must append here too.
/// </summary>
public sealed class StockAdjustment
{
    public Guid PublicId { get; set; } = Guid.CreateVersion7();

    public Guid ProductId { get; set; }

    /// <summary>Signed delta applied to OnHand.</summary>
    public int Delta { get; set; }

    public string? Reason { get; set; }

    public string? ReferenceType { get; set; }

    public Guid? ReferenceId { get; set; }

    public Guid? ActorId { get; set; }

    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Ordered media attached to a product (domain-model §4.3 child). Each row
/// points at a content-addressed MediaAsset; ordering and the primary flag drive
/// gallery and thumbnail rendering.
/// </summary>
public sealed class ProductMedia
{
    public Guid PublicId { get; set; } = Guid.CreateVersion7();

    public Guid ProductId { get; set; }

    public Guid MediaAssetId { get; set; }

    public int SortOrder { get; set; }

    public bool IsPrimary { get; set; }

    /// <summary>Accessible alt text for the asset in this placement.</summary>
    public string? AltText { get; set; }
}