using Stockpile.Application.Audit;
using Stockpile.Application.Common.Errors;
using Stockpile.Contracts.Errors;
using Stockpile.Domain.Enums;

namespace Stockpile.Application.Suppliers;

/// <summary>
/// Use cases for the signed-in supplier's own onboarding surface
/// (business-flows §3.3 stages 2–5, api.md §2.9). The request filter validates
/// the shape, this service resolves the caller's supplier, normalises the
/// payload and records the audit trail, and the store persists it. A caller
/// with no supplier answers one 404 <c>RESOURCE_NOT_FOUND</c> — another
/// supplier's existence is never inferable (BOLA, api.md §1.6).
/// </summary>
public sealed class SupplierSelfService(
    ISupplierSelfStore store,
    IAuditService audit)
{
    /// <summary>The caller's own supplier (the onboarding read).</summary>
    public async Task<SupplierSelfResponse> GetSelfAsync(Guid userId, CancellationToken ct)
    {
        Guid supplierId = await RequireSupplierAsync(userId, ct);
        return await store.GetSelfAsync(supplierId, ct) ?? throw NotFound();
    }

    /// <summary>Merges stages 2–3 (person + business) and answers with the new state.</summary>
    public async Task<SupplierSelfResponse> UpdateProfileAsync(
        Guid userId, UpdateSupplierProfileRequest request, CancellationToken ct)
    {
        Guid supplierId = await RequireSupplierAsync(userId, ct);

        SupplierProfileDraft draft = new(
            Keep(request.CompanyName),
            Keep(request.RegistrationNumber),
            Keep(request.Website),
            Keep(request.ContactPerson),
            Keep(request.Phone),
            Keep(request.Email));

        await store.UpdateProfileAsync(supplierId, draft, ct);
        await RecordAsync(supplierId, "supplier.profile.updated", ct);

        return await store.GetSelfAsync(supplierId, ct) ?? throw NotFound();
    }

    /// <summary>Adds one category to the supplier's coverage (stage 5, idempotent).</summary>
    public async Task<SupplierSelfResponse> AddCategoryAsync(Guid userId, string slug, CancellationToken ct)
    {
        Guid supplierId = await RequireSupplierAsync(userId, ct);
        string normalized = slug.Trim();

        Guid categoryId = await store.ResolveCategoryIdAsync(normalized, ct)
            ?? throw UnknownCategory(normalized);

        await store.AddCategoryAsync(supplierId, categoryId, ct);
        await RecordAsync(supplierId, "supplier.categories.updated", ct);

        return await store.GetSelfAsync(supplierId, ct) ?? throw NotFound();
    }

    /// <summary>Removes one category from the supplier's coverage (stage 5).</summary>
    public async Task<SupplierSelfResponse> RemoveCategoryAsync(Guid userId, string slug, CancellationToken ct)
    {
        Guid supplierId = await RequireSupplierAsync(userId, ct);

        if (!await store.RemoveCategoryAsync(supplierId, slug, ct))
        {
            throw new AppException(
                ErrorCodes.ResourceNotFound, "The supplier does not cover that category.");
        }

        await RecordAsync(supplierId, "supplier.categories.updated", ct);
        return await store.GetSelfAsync(supplierId, ct) ?? throw NotFound();
    }

    /// <summary>Replaces the supplier's district service areas (stage 4).</summary>
    public async Task<SupplierSelfResponse> SetServiceAreasAsync(
        Guid userId, SetSupplierServiceAreasRequest request, CancellationToken ct)
    {
        Guid supplierId = await RequireSupplierAsync(userId, ct);

        List<string> districts = NormalizeList(request.Districts);
        if (!await store.AllDistrictsExistAsync(districts, ct))
        {
            throw new AppException(
                ErrorCodes.ValidationFailed,
                [new FieldError("districts", "unknown_district", "Choose districts from the Sri Lanka reference list.")],
                "One or more districts are not recognised.");
        }

        string? defaultDistrict = string.IsNullOrWhiteSpace(request.DefaultDistrict)
            ? null
            : request.DefaultDistrict.Trim();

        if (defaultDistrict is not null && !districts.Contains(defaultDistrict, StringComparer.Ordinal))
        {
            throw new AppException(
                ErrorCodes.ValidationFailed,
                [new FieldError("defaultDistrict", "not_selected", "Choose one of the districts you selected.")],
                "The default district must be one of the chosen districts.");
        }

        await store.SetServiceAreasAsync(supplierId, districts, defaultDistrict, ct);
        await RecordAsync(supplierId, "supplier.service-areas.updated", ct);

        return await store.GetSelfAsync(supplierId, ct) ?? throw NotFound();
    }

    private async Task<Guid> RequireSupplierAsync(Guid userId, CancellationToken ct)
        => await store.ResolveSupplierIdAsync(userId, ct) ?? throw NotFound();

    private Task RecordAsync(Guid supplierId, string action, CancellationToken ct)
        => audit.RecordAsync(
            new AuditEntry(action, "Supplier", supplierId.ToString(), AuditResult.Success), ct);

    /// <summary>Trims, drops blanks and removes duplicates (ordinal, first occurrence wins).</summary>
    private static List<string> NormalizeList(IReadOnlyList<string> values)
    {
        List<string> normalized = [];
        foreach (string value in values)
        {
            string trimmed = value.Trim();
            if (trimmed.Length == 0 || normalized.Contains(trimmed, StringComparer.Ordinal))
            {
                continue;
            }

            normalized.Add(trimmed);
        }

        return normalized;
    }

    /// <summary>
    /// The store's merge signal: a <c>null</c> member is untouched, a blank
    /// string clears the column, anything else is the trimmed new value.
    /// </summary>
    private static string? Keep(string? value) => value?.Trim();

    private static AppException NotFound() => new(ErrorCodes.ResourceNotFound, "The signed-in account is not a supplier.");

    private static AppException UnknownCategory(string slug) => new(
        ErrorCodes.ValidationFailed,
        [new FieldError("slug", "unknown_category", $"'{slug}' is not a category in the catalogue.")],
        "The catalogue does not know that category.");
}
