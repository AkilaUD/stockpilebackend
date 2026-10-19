namespace Stockpile.Contracts.Procurement;

/// <summary>
/// Create-and-draft an RFQ (api.md §2.6 <c>POST /rfqs</c>). Line items ride
/// inline so one submit carries the whole request; a buyer with no organisation
/// yet supplies <see cref="Company"/> and the platform provisions the buyer
/// company (business-flows §2 step 3) in the same call. Optional members are
/// omitted, never null (api.md §1.4).
/// </summary>
public sealed record CreateRfqRequest(
    string Title,
    RfqCompanyDraft? Company,
    string? DeliveryLocationText,
    bool SubstitutionsAllowed,
    string? SpecialInstructions,
    int? ExpiryDays,
    IReadOnlyList<CreateRfqItemRequest> Items);

/// <summary>Company identity for the auto-provisioned buyer organisation (domain-model §2.3 column lengths).</summary>
public sealed record RfqCompanyDraft(
    string Name,
    string? RegistrationNumber,
    string? Website);

/// <summary>
/// One requested line (domain-model §5.3 <c>RFQItem</c>): free-text description
/// plus quantity; <c>CategorySlug</c> selects the department the request is
/// matched in, <c>UnitOfMeasureCode</c> the seeded unit.
/// </summary>
public sealed record CreateRfqItemRequest(
    string Description,
    decimal Quantity,
    string? CategorySlug,
    string? UnitOfMeasureCode,
    string? Specification);

/// <summary>
/// An RFQ on the wire (api.md §2.6). <c>Status</c> is the enum name; the item
/// and match collections are the buyer's own view of the request.
/// </summary>
public sealed record RfqResponse(
    string Id,
    string ReferenceCode,
    string Title,
    string Status,
    string OrganizationId,
    string Visibility,
    string Source,
    bool SubstitutionsAllowed,
    string? DeliveryLocationText,
    string? SpecialInstructions,
    string? SubmittedAt,
    string? ExpiryAt,
    IReadOnlyList<RfqItemResponse> Items,
    IReadOnlyList<RfqMatchSummary> Matches);

/// <summary>One line item on the wire; <c>CategorySlug</c>/<c>UnitOfMeasure</c> are resolved display values.</summary>
public sealed record RfqItemResponse(
    string Id,
    int LineNumber,
    string Description,
    decimal Quantity,
    string? CategorySlug,
    string? UnitOfMeasure,
    string? Specification);

/// <summary>A matched supplier (the buyer's dispatch view, §3.8 step 12) — count is data-driven, never promised.</summary>
public sealed record RfqMatchSummary(
    string SupplierId,
    string SupplierName,
    string SupplierSlug,
    string Status,
    decimal MatchScore,
    string MatchReasons);
