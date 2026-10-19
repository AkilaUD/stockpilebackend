using Stockpile.Domain.Enums;

namespace Stockpile.Application.Suppliers;

/// <summary>
/// The signed-in supplier's own surface (api.md §2.9, business-flows §3.3
/// stages 2–5). Supplier self-registration collects the account, the person,
/// the business, the coverage and the service areas in one atomic submit; this
/// surface is what makes every stage after the account **resumable and
/// editable**: the contact/business profile (stages 2–3), the district service
/// areas (stage 4) and the category coverage (stage 5). The caller's supplier
/// is resolved from their own accepted organisation membership, so one supplier
/// can never read or write another's rows (BOLA, api.md §1.6).
/// </summary>

/// <summary>The caller's supplier as the onboarding/edit surface sees it.</summary>
public sealed record SupplierSelfResponse(
    Guid SupplierId,
    string Slug,
    string DisplayName,
    SupplierStatus Status,
    VerificationState VerificationState,
    string CompanyName,
    string? RegistrationNumber,
    string? Website,
    SupplierProfileResponse Profile,
    IReadOnlyList<SupplierCategoryResponse> Categories,
    IReadOnlyList<SupplierServiceAreaResponse> ServiceAreas);

/// <summary>Stage 2/3 contact rows (public profile data only).</summary>
public sealed record SupplierProfileResponse(
    string? ContactPerson,
    string? Phone,
    string? Email,
    string? Website);

/// <summary>One category the supplier is on record as covering (stage 5).</summary>
public sealed record SupplierCategoryResponse(
    string Slug,
    string Name,
    SpecialtyLevel SpecialtyLevel,
    bool Quotable);

/// <summary>One district the supplier serves (stage 4).</summary>
public sealed record SupplierServiceAreaResponse(
    string DistrictCode,
    bool IsDefault,
    int? RadiusKm);

/// <summary>
/// Partial merge of stages 2–3: a <c>null</c> member is left untouched and a
/// blank string clears it, so a step never has to read a value back before
/// writing the one it changed.
/// </summary>
public sealed record UpdateSupplierProfileRequest(
    string? CompanyName = null,
    string? RegistrationNumber = null,
    string? Website = null,
    string? ContactPerson = null,
    string? Phone = null,
    string? Email = null);

/// <summary>Stage 5 add: the catalogue category slug to cover.</summary>
public sealed record AddSupplierCategoryRequest(string Slug);

/// <summary>Stage 4 replace: the full set of district codes the supplier serves.</summary>
public sealed record SetSupplierServiceAreasRequest(
    IReadOnlyList<string> Districts,
    string? DefaultDistrict = null);

/// <summary>Normalised stage 2–3 merge handed to the store (null = untouched, "" = clear).</summary>
public sealed record SupplierProfileDraft(
    string? CompanyName,
    string? RegistrationNumber,
    string? Website,
    string? ContactPerson,
    string? Phone,
    string? Email);
