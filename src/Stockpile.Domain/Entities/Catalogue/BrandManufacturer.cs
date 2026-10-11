using Stockpile.Domain.Enums;

namespace Stockpile.Domain.Entities.Catalogue;

/// <summary>Brand definition (domain-model §4.6). Code-keyed; shared across products.</summary>
public sealed class Brand
{
    public Guid PublicId { get; set; } = Guid.CreateVersion7();

    /// <summary>Stable code, e.g. <c>generic</c>; unique.</summary>
    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? Website { get; set; }

    public Guid? LogoMediaId { get; set; }

    /// <summary>Country of brand registration (ISO alpha-2 code row).</summary>
    public string? CountryCode { get; set; }
}

/// <summary>Manufacturer definition (domain-model §4.6). Distinct from Brand.</summary>
public sealed class Manufacturer
{
    public Guid PublicId { get; set; } = Guid.CreateVersion7();

    /// <summary>Stable code; unique.</summary>
    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? Website { get; set; }

    /// <summary>Country of manufacture (ISO alpha-2 code row).</summary>
    public string? CountryCode { get; set; }
}

/// <summary>Unit of measure definition (domain-model §4.6). Code-keyed.</summary>
public sealed class UnitOfMeasure
{
    public Guid PublicId { get; set; } = Guid.CreateVersion7();

    /// <summary>Stable code, e.g. <c>each</c> or <c>sqm</c>; unique.</summary>
    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string PluralName { get; set; } = string.Empty;

    public DimensionKind DimensionKind { get; set; } = DimensionKind.Count;
}