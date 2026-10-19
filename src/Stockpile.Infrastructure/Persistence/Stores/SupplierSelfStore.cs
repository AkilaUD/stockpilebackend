using Microsoft.EntityFrameworkCore;
using Stockpile.Application.Suppliers;
using Stockpile.Domain.Entities.Catalogue;
using Stockpile.Domain.Entities.Identity;
using Stockpile.Domain.Enums;

namespace Stockpile.Infrastructure.Persistence.Stores;

/// <summary>
/// EF implementation of <see cref="ISupplierSelfStore"/> (api.md §2.9,
/// business-flows §3.3 stages 2–5). Every query is keyed to a supplier id the
/// caller has already been proven to own (resolved from their accepted
/// organisation membership), so one supplier can never read or write another's
/// rows (BOLA, api.md §1.6). The profile merge lives on two tables — the
/// organisation carries the registered business identity and
/// <c>SupplierProfiles</c> the public contact rows — and both are written in one
/// <see cref="StockpileDbContext.SaveChangesAsync(CancellationToken)"/>.
/// </summary>
public sealed class SupplierSelfStore(StockpileDbContext db) : ISupplierSelfStore
{
    /// <inheritdoc />
    public async Task<Guid?> ResolveSupplierIdAsync(Guid userId, CancellationToken cancellationToken)
    {
        Guid supplierId = await (
                from member in db.OrganizationMembers
                where member.UserId == userId && member.InvitationStatus == InvitationStatus.Accepted
                join supplier in db.Suppliers on member.OrganizationId equals supplier.OrganizationId
                select supplier.PublicId)
            .FirstOrDefaultAsync(cancellationToken);

        return supplierId == Guid.Empty ? null : supplierId;
    }

    /// <inheritdoc />
    public async Task<SupplierSelfResponse?> GetSelfAsync(Guid supplierId, CancellationToken cancellationToken)
    {
        Supplier? supplier = await db.Suppliers
            .AsNoTracking()
            .Include(candidate => candidate.Profile)
            .FirstOrDefaultAsync(candidate => candidate.PublicId == supplierId, cancellationToken);

        if (supplier is null)
        {
            return null;
        }

        Organization? organization = await db.Organizations
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.PublicId == supplier.OrganizationId, cancellationToken);

        SupplierProfile profile = supplier.Profile ?? new SupplierProfile { SupplierId = supplierId };

        return new SupplierSelfResponse(
            supplier.PublicId,
            supplier.Slug,
            supplier.DisplayName,
            supplier.Status,
            supplier.VerificationState,
            organization?.Name ?? supplier.DisplayName,
            organization?.RegistrationNumber,
            organization?.Website ?? profile.Website,
            new SupplierProfileResponse(profile.ContactPerson, profile.Phone, profile.Email, profile.Website),
            await GetCategoriesAsync(supplierId, cancellationToken),
            await GetServiceAreasAsync(supplierId, cancellationToken));
    }

    /// <inheritdoc />
    public async Task<SupplierProfileResponse?> UpdateProfileAsync(
        Guid supplierId, SupplierProfileDraft draft, CancellationToken cancellationToken)
    {
        Supplier? supplier = await db.Suppliers
            .Include(candidate => candidate.Profile)
            .FirstOrDefaultAsync(candidate => candidate.PublicId == supplierId, cancellationToken);

        if (supplier is null)
        {
            return null;
        }

        Organization? organization = await db.Organizations
            .FirstOrDefaultAsync(candidate => candidate.PublicId == supplier.OrganizationId, cancellationToken);

        // Stage 3 — the registered business identity (organisation) and the
        // public display name (supplier) stay in step.
        if (draft.CompanyName is not null)
        {
            supplier.DisplayName = draft.CompanyName;
            if (organization is not null)
            {
                organization.Name = draft.CompanyName;
            }
        }

        if (organization is not null)
        {
            if (draft.RegistrationNumber is not null)
            {
                organization.RegistrationNumber = NullIfBlank(draft.RegistrationNumber);
            }

            if (draft.Website is not null)
            {
                organization.Website = NullIfBlank(draft.Website);
            }
        }

        // Stage 2 — the public contact rows.
        SupplierProfile profile = supplier.Profile ?? new SupplierProfile { SupplierId = supplierId };
        if (supplier.Profile is null)
        {
            supplier.Profile = profile;
            db.SupplierProfiles.Add(profile);
        }

        if (draft.ContactPerson is not null)
        {
            profile.ContactPerson = NullIfBlank(draft.ContactPerson);
        }

        if (draft.Phone is not null)
        {
            profile.Phone = NullIfBlank(draft.Phone);
        }

        if (draft.Email is not null)
        {
            profile.Email = NullIfBlank(draft.Email);
        }

        if (draft.Website is not null)
        {
            profile.Website = NullIfBlank(draft.Website);
        }

        await db.SaveChangesAsync(cancellationToken);

        return new SupplierProfileResponse(profile.ContactPerson, profile.Phone, profile.Email, profile.Website);
    }

    /// <inheritdoc />
    public async Task<Guid?> ResolveCategoryIdAsync(string slug, CancellationToken cancellationToken)
    {
        string normalized = slug.Trim();
        Guid categoryId = await db.Categories
            .Where(category => category.Slug == normalized && category.IsActive)
            .Select(category => category.PublicId)
            .FirstOrDefaultAsync(cancellationToken);

        return categoryId == Guid.Empty ? null : categoryId;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SupplierCategoryResponse>?> AddCategoryAsync(
        Guid supplierId, Guid categoryId, CancellationToken cancellationToken)
    {
        bool supplierExists = await db.Suppliers.AnyAsync(supplier => supplier.PublicId == supplierId, cancellationToken);
        if (!supplierExists)
        {
            return null;
        }

        bool covered = await db.SupplierCategories
            .AnyAsync(coverage => coverage.SupplierId == supplierId && coverage.CategoryId == categoryId, cancellationToken);
        if (!covered)
        {
            db.SupplierCategories.Add(new SupplierCategory { SupplierId = supplierId, CategoryId = categoryId });
            await db.SaveChangesAsync(cancellationToken);
        }

        return await GetCategoriesAsync(supplierId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> RemoveCategoryAsync(Guid supplierId, string slug, CancellationToken cancellationToken)
    {
        string normalized = slug.Trim();
        Guid? categoryId = await ResolveCategoryIdAsync(normalized, cancellationToken);
        if (categoryId is null)
        {
            return false;
        }

        int removed = await db.SupplierCategories
            .Where(coverage => coverage.SupplierId == supplierId && coverage.CategoryId == categoryId)
            .ExecuteDeleteAsync(cancellationToken);

        return removed > 0;
    }

    /// <inheritdoc />
    public async Task<bool> AllDistrictsExistAsync(
        IReadOnlyList<string> districtCodes, CancellationToken cancellationToken)
    {
        if (districtCodes.Count == 0)
        {
            return true;
        }

        int known = await db.Districts
            .CountAsync(district => districtCodes.Contains(district.Code), cancellationToken);

        return known == districtCodes.Count;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SupplierServiceAreaResponse>?> SetServiceAreasAsync(
        Guid supplierId, IReadOnlyList<string> districtCodes, string? defaultDistrict, CancellationToken cancellationToken)
    {
        bool supplierExists = await db.Suppliers.AnyAsync(supplier => supplier.PublicId == supplierId, cancellationToken);
        if (!supplierExists)
        {
            return null;
        }

        // Replace-in-one-transaction: the delete runs immediately, so it must be
        // inside the transaction that inserts the replacement set (the same
        // discipline the registration store applies to its single write).
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        await db.SupplierServiceAreas
            .Where(area => area.SupplierId == supplierId)
            .ExecuteDeleteAsync(cancellationToken);

        foreach (string districtCode in districtCodes)
        {
            db.SupplierServiceAreas.Add(new SupplierServiceArea
            {
                SupplierId = supplierId,
                DistrictCode = districtCode,
                IsDefault = defaultDistrict is not null
                    && string.Equals(districtCode, defaultDistrict, StringComparison.Ordinal),
            });
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return await GetServiceAreasAsync(supplierId, cancellationToken);
    }

    private async Task<IReadOnlyList<SupplierCategoryResponse>> GetCategoriesAsync(
        Guid supplierId, CancellationToken cancellationToken)
    {
        var rows = await db.SupplierCategories
            .AsNoTracking()
            .Where(coverage => coverage.SupplierId == supplierId)
            .Join(
                db.Categories,
                coverage => coverage.CategoryId,
                category => category.PublicId,
                (coverage, category) => new
                {
                    coverage.SpecialtyLevel,
                    coverage.Quotable,
                    category.Slug,
                    category.Name,
                    category.SortOrder,
                })
            .ToListAsync(cancellationToken);

        return rows
            .OrderBy(row => row.SortOrder)
            .ThenBy(row => row.Name, StringComparer.Ordinal)
            .Select(row => new SupplierCategoryResponse(row.Slug, row.Name, row.SpecialtyLevel, row.Quotable))
            .ToList();
    }

    private async Task<IReadOnlyList<SupplierServiceAreaResponse>> GetServiceAreasAsync(
        Guid supplierId, CancellationToken cancellationToken)
    {
        var rows = await db.SupplierServiceAreas
            .AsNoTracking()
            .Where(area => area.SupplierId == supplierId)
            .OrderByDescending(area => area.IsDefault)
            .ThenBy(area => area.DistrictCode)
            .Select(area => new { area.DistrictCode, area.IsDefault, area.RadiusKm })
            .ToListAsync(cancellationToken);

        return rows
            .Select(row => new SupplierServiceAreaResponse(row.DistrictCode, row.IsDefault, row.RadiusKm))
            .ToList();
    }

    /// <summary>A blank provided value clears the stored column (partial-merge semantics).</summary>
    private static string? NullIfBlank(string value) => value.Trim().Length == 0 ? null : value.Trim();
}
