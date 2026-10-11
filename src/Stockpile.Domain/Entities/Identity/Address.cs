namespace Stockpile.Domain.Entities.Identity;

/// <summary>
/// Postal address (domain-model §2.4). <c>District</c> and <c>Province</c> hold
/// reference-data codes (not free text) so filtering is by code.
/// Owned by exactly one organisation or user — enforced by a CHECK constraint.
/// </summary>
public sealed class Address
{
    /// <summary>Guid; appears in address CRUD paths (api.md public-id rule).</summary>
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public string Line1 { get; set; } = string.Empty;

    public string? Line2 { get; set; }

    public string City { get; set; } = string.Empty;

    /// <summary>District code, e.g. <c>colombo</c> (FK → District.Code).</summary>
    public string District { get; set; } = string.Empty;

    /// <summary>Province code, e.g. <c>western</c> (FK → Province.Code).</summary>
    public string Province { get; set; } = string.Empty;

    public string? PostalCode { get; set; }

    /// <summary>ISO 3166-1 alpha-2 (FK → Country.Code).</summary>
    public string CountryCode { get; set; } = "LK";

    public decimal? Latitude { get; set; }

    public decimal? Longitude { get; set; }

    public bool IsDefaultBilling { get; set; }

    public bool IsDefaultDelivery { get; set; }

    /// <summary>Set when the organisation owns the address.</summary>
    public Guid? OrganizationId { get; set; }

    /// <summary>Set when the user owns the address. Exactly one owner (CHECK).</summary>
    public Guid? UserId { get; set; }
}
