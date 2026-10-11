using Stockpile.Domain.Common;
using Stockpile.Domain.Enums;

namespace Stockpile.Domain.Entities.Catalogue;

/// <summary>
/// Aggregate root (domain-model §3.1). A supplier is the catalogue-side face of
/// an <see cref="Stockpile.Domain.Entities.Identity.Organization"/> of type
/// <c>SupplierCompany</c> — one supplier per organisation, always member-managed.
/// Slug is unique and immutable after first publication (public URL stability).
/// </summary>
public sealed class Supplier : AuditableEntity
{
    public Guid PublicId { get; set; } = Guid.CreateVersion7();

    /// <summary>Owning organisation (Identity). Null for marketplaces-listed
    /// suppliers that have not onboarded to a Stockpile account yet; bound on
    /// onboarding (one supplier per organisation).</summary>
    public Guid? OrganizationId { get; set; }

    /// <summary>Unique public slug used in supplier profile URLs, e.g. <c>asian-plywoods</c>.</summary>
    public string Slug { get; set; } = string.Empty;

    /// <summary>Public shop name, e.g. <c>Asian Plywoods (Pvt) Ltd</c>.</summary>
    public string DisplayName { get; set; } = string.Empty;

    public SupplierStatus Status { get; set; } = SupplierStatus.Active;

    /// <summary>Orthogonal to status (domain-model §3.1).</summary>
    public VerificationState VerificationState { get; set; } = VerificationState.Unverified;

    /// <summary>Company story as harvested from the live profile page.</summary>
    public string? Story { get; set; }

    public Guid? LogoMediaId { get; set; }

    public Guid? BannerMediaId { get; set; }

    public int? FoundedYear { get; set; }

    /// <summary>Declared band, e.g. <c>11–50</c>; validated against config ranges later.</summary>
    public string? EmployeeBand { get; set; }

    /// <summary>Default audience policy applied to every product until overridden (§3.4).</summary>
    public SupplierAudience DefaultAudiencePolicy { get; set; } = SupplierAudience.Everyone;

    public int? ServiceRadiusKm { get; set; }

    /// <summary>SQL rowversion for optimistic concurrency on this aggregate root.</summary>
    public byte[] RowVersion { get; set; } = [];

    /// <summary>Public contact/profile rows harvested from the live supplier page (best-effort).</summary>
    public SupplierProfile? Profile { get; set; }
}

/// <summary>
/// Publicly published contact and address rows for a supplier (domain-model
/// §3 — public profile data only; anything privacy-marked is never stored here).
/// </summary>
public sealed class SupplierProfile
{
    public Guid SupplierId { get; set; }

    public string? ContactPerson { get; set; }

    public string? Phone { get; set; }

    public string? Email { get; set; }

    public string? Website { get; set; }

    public string? OperationAddress { get; set; }

    public string? CorporateAddress { get; set; }

    /// <summary>Map pin published on the live supplier profile (public data).</summary>
    public double? Latitude { get; set; }

    public double? Longitude { get; set; }
}

/// <summary>Supplier ↔ category relation driving RFQ matching (domain-model §3.3).</summary>
public sealed class SupplierCategory
{
    public Guid PublicId { get; set; } = Guid.CreateVersion7();

    public Guid SupplierId { get; set; }

    public Guid CategoryId { get; set; }

    public SpecialtyLevel SpecialtyLevel { get; set; } = SpecialtyLevel.Primary;

    /// <summary>False = the supplier does not accept RFQs in this category.</summary>
    public bool Quotable { get; set; } = true;
}

/// <summary>
/// Audience policy for a supplier's catalogue exposure (domain-model §3.4).
/// Default <c>Everyone/AllProducts/FullDetails</c>; every price/stock query is
/// filtered against this in SQL — a hidden price never reaches the client.
/// </summary>
public sealed class SupplierAudiencePolicy
{
    public Guid SupplierId { get; set; }

    public SupplierAudience Audience { get; set; } = SupplierAudience.Everyone;

    public AudienceAppliesTo AppliesTo { get; set; } = AudienceAppliesTo.AllProducts;

    public SupplierVisibility Visibility { get; set; } = SupplierVisibility.FullDetails;
}

/// <summary>District-level service area of a supplier (domain-model §3, matches live coverage).</summary>
public sealed class SupplierServiceArea
{
    public Guid PublicId { get; set; } = Guid.CreateVersion7();

    public Guid SupplierId { get; set; }

    /// <summary>District code from the Sri Lanka reference geography, e.g. <c>colombo</c>.</summary>
    public string DistrictCode { get; set; } = string.Empty;

    public bool IsDefault { get; set; }

    public int? RadiusKm { get; set; }
}

/// <summary>
/// Append-only verification ledger (domain-model §3.2). Every decision is a
/// §13.16 audit target; documents referenced here live in private storage.
/// </summary>
public sealed class SupplierVerification
{
    public Guid PublicId { get; set; } = Guid.CreateVersion7();

    public Guid SupplierId { get; set; }

    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;

    public Guid? RequestedBy { get; set; }

    public Guid? DocumentSetId { get; set; }

    public DateTime? DecisionAt { get; set; }

    public Guid? DecisionBy { get; set; }

    public VerificationDecision Decision { get; set; } = VerificationDecision.None;

    public string? Reason { get; set; }

    public DateTime? ExpiresAt { get; set; }
}

/// <summary>Badge shown on the supplier's public page (e.g. <c>verified</c>).</summary>
public sealed class SupplierBadge
{
    public Guid PublicId { get; set; } = Guid.CreateVersion7();

    public Guid SupplierId { get; set; }

    /// <summary>Stable key, e.g. <c>verified</c>, unique per supplier.</summary>
    public string BadgeKey { get; set; } = string.Empty;

    public string? Label { get; set; }

    public DateTime GrantedAt { get; set; } = DateTime.UtcNow;

    public string? Reason { get; set; }
}

/// <summary>Microsite appearance settings (domain-model §3.6).</summary>
public sealed class SupplierPublicPageConfig
{
    public Guid SupplierId { get; set; }

    public string? HeroHeadline { get; set; }

    public Guid? HeroImageMediaId { get; set; }

    public bool ShowProducts { get; set; } = true;

    public bool ShowProjects { get; set; }

    public bool ShowReviews { get; set; }

    public bool ShowPolicies { get; set; }

    public string? PrimaryCtaLabel { get; set; }

    /// <summary>Extra microsite sections as JSON text (jsonb-equivalent).</summary>
    public string? CustomSections { get; set; }
}