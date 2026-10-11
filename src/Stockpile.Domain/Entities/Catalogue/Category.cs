namespace Stockpile.Domain.Entities.Catalogue;

/// <summary>
/// Aggregate root (domain-model §4.1): the catalogue's category tree. Children
/// of a category are categories (self-referencing ParentId); products link to
/// exactly one category and CategoryAttributeDefinition links a category to the
/// attribute schema its products must honour (§4.2).
/// </summary>
public sealed class Category
{
    public Guid PublicId { get; set; } = Guid.CreateVersion7();

    /// <summary>Parent node; null on a root (department or department-level) node.</summary>
    public Guid? ParentId { get; set; }

    /// <summary>URL-safe unique slug, e.g. <c>building-materials/waterproofing</c>.</summary>
    public string Slug { get; set; } = string.Empty;

    /// <summary>Display name in the catalogue's default language (harvested from the live site).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Sinhala display name where the live site publishes one.
    /// Schema note: domain-model §4.1 declares a single Name; the SI store-view
    /// names that stockpile.lk publishes are kept here so every locale route can
    /// render them (design-system §6.1). Null until the SI name is harvested.
    /// </summary>
    public string? NameSi { get; set; }

    public string? Description { get; set; }

    /// <summary>Header/artwork media (MediaAsset public id).</summary>
    public Guid? ImageMediaId { get; set; }

    /// <summary>Design-system icon key for the department grid, e.g. <c>building-materials</c>.</summary>
    public string? IconKey { get; set; }

    public int SortOrder { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>Procurement metaphor collection this category belongs to (design-system §8.3, §21). Null until configured.</summary>
    public string? ProcurementMetaphor { get; set; }

    /// <summary>Materialized path, e.g. <c>building-materials/waterproofing</c>; indexed for tree reads (§4.1).</summary>
    public string MaterializedPath { get; set; } = string.Empty;
}