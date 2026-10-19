namespace Stockpile.Domain.Enums;

/// <summary>
/// Content item kind (domain-model §9). The reference publishes news, completed
/// project write-ups, supplier stories and guide articles, so one table carries
/// them all — the kind is what selects the surface they appear on.
/// </summary>
public enum ContentKind
{
    NewsArticle,
    ProjectCaseStudy,
    SupplierStory,
    CategoryGuide,
    MaterialGuide,
    ProcurementEducation,
    SupplierSpotlight,
    TechnicalArticle,
}

/// <summary>Publication state (domain-model §9). Only <c>Published</c> rows are served publicly.</summary>
public enum ContentStatus
{
    Draft,
    Scheduled,
    Published,
    Archived,
}
