namespace Stockpile.Domain.Entities.Reference;

/// <summary>ISO 3166-1 country reference (domain-model §4.6). Seeded; never edited at runtime.</summary>
public sealed class Country
{
    /// <summary>ISO 3166-1 alpha-2, e.g. <c>LK</c>.</summary>
    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>ISO 3166-1 numeric, nvarchar(3).</summary>
    public string NumericCode { get; set; } = string.Empty;
}

/// <summary>Sri Lankan province reference (domain-model §2.4). Code-keyed, never free text.</summary>
public sealed class Province
{
    /// <summary>Slug code, e.g. <c>western</c>.</summary>
    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
}

/// <summary>Sri Lankan district reference (domain-model §2.4). Code-keyed, never free text.</summary>
public sealed class District
{
    /// <summary>Slug code, e.g. <c>colombo</c>.</summary>
    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>Owning province code (FK → Province.Code).</summary>
    public string ProvinceCode { get; set; } = string.Empty;
}
