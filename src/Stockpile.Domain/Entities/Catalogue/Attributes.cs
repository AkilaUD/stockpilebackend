using Stockpile.Domain.Enums;

namespace Stockpile.Domain.Entities.Catalogue;

/// <summary>
/// Attribute definition (domain-model §4.2 hybrid model). Definitions are
/// shared across categories; CategoryAttributeDefinition decides which
/// categories use them and how. Typed values live on ProductAttributeValue.
/// </summary>
public sealed class AttributeDefinition
{
    public Guid PublicId { get; set; } = Guid.CreateVersion7();

    /// <summary>Stable machine code, e.g. <c>lead_time_days</c>, unique across the catalogue.</summary>
    public string Code { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public AttributeDataType DataType { get; set; } = AttributeDataType.Text;

    /// <summary>For Number/Time attributes: the UOM the value is expressed in.</summary>
    public Guid? UnitOfMeasureId { get; set; }

    /// <summary>System attributes are protected from deletion/editing by catalogue admins.</summary>
    public bool IsSystem { get; set; }

    public string? HelpText { get; set; }

    /// <summary>Optional display formatting hint, e.g. <c>N2</c>.</summary>
    public string? DisplayFormat { get; set; }

    /// <summary>Include the value in full-text search projections.</summary>
    public bool Searchable { get; set; }
}

/// <summary>Allowed value of an Enum/MultiSelect attribute (domain-model §4.2).</summary>
public sealed class AttributeOption
{
    public Guid PublicId { get; set; } = Guid.CreateVersion7();

    public Guid AttributeId { get; set; }

    /// <summary>Stable code used in URLs/filters, e.g. <c>steel</c>.</summary>
    public string Code { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public int SortOrder { get; set; }

    /// <summary>Optional colour swatch in <c>#RRGGBB</c>.</summary>
    public string? Swatch { get; set; }
}

/// <summary>
/// Category ↔ attribute join including filter/sort behaviour (domain-model
/// §4.2). Uniqueness on (CategoryId, AttributeId) is enforced in configuration.
/// </summary>
public sealed class CategoryAttributeDefinition
{
    public Guid CategoryId { get; set; }

    public Guid AttributeId { get; set; }

    public bool IsRequired { get; set; }

    public bool IsFilterable { get; set; }

    public bool IsSortable { get; set; }

    public bool IsComparable { get; set; }

    public int SortOrder { get; set; }

    public string? DefaultValue { get; set; }

    public ItemType AppliesToItemType { get; set; } = ItemType.PhysicalProduct;
}