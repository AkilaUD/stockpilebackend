using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Stockpile.Application.Suppliers;
using Stockpile.Domain.Entities.Catalogue;
using Stockpile.Domain.Entities.Identity;
using Stockpile.Domain.Enums;

namespace Stockpile.Infrastructure.Persistence;

/// <summary>
/// Task 2F — <see cref="ISupplierRegistrationStore"/> over the schema's
/// <c>Organizations</c>, <c>OrganizationMembers</c>, <c>Suppliers</c>,
/// <c>SupplierProfiles</c> and <c>SupplierCategories</c> tables (domain-model
/// §2.3/§3.1/§3.3). The organisation, the caller's accepted membership, the
/// supplier aggregate and its category coverage commit in ONE transaction, so
/// an abandoned submit never leaves a half-built supplier — the same discipline
/// <see cref="OrganizationStore"/> applies to the buyer wizard.
/// </summary>
public sealed class SupplierRegistrationStore : ISupplierRegistrationStore
{
    /// <summary>Seeded role code for a supplier's primary owner (appsettings Seed:Roles).</summary>
    private const string SupplierOwnerRoleCode = "supplierOwner";

    private readonly StockpileDbContext _db;
    private readonly TimeProvider _time;

    public SupplierRegistrationStore(StockpileDbContext db, TimeProvider time)
    {
        _db = db;
        _time = time;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Guid>> ResolveCategoryIdsAsync(
        IReadOnlyList<string> slugs, CancellationToken cancellationToken)
    {
        if (slugs.Count == 0)
        {
            return [];
        }

        List<string> wanted = slugs
            .Where(slug => !string.IsNullOrWhiteSpace(slug))
            .Select(slug => slug.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (wanted.Count == 0)
        {
            return [];
        }

        return await _db.Categories
            .Where(category => wanted.Contains(category.Slug) && category.IsActive)
            .Select(category => category.PublicId)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Guid> CreateSupplierAsync(
        SupplierRegistrationDraft draft, CancellationToken cancellationToken)
    {
        await using IDbContextTransaction transaction =
            await _db.Database.BeginTransactionAsync(cancellationToken);

        var organization = new Organization
        {
            Name = Required(draft.CompanyName, 200),
            Type = OrganizationType.SupplierCompany,
            RegistrationNumber = Optional(draft.RegistrationNumber, 60),
            Website = Optional(draft.Website, 300),
            Status = OrganizationStatus.Active,
        };
        _db.Organizations.Add(organization);

        _db.OrganizationMembers.Add(new OrganizationMember
        {
            OrganizationId = organization.PublicId,
            UserId = draft.UserId,
            RoleCode = SupplierOwnerRoleCode,
            InvitationStatus = InvitationStatus.Accepted,
            JoinedAt = _time.GetUtcNow().UtcDateTime,
        });

        // No Status assignment: the entity's declared default is Active (the
        // domain's landing state for a vetted supplier), and X6/S10 forbids a
        // direct workflow assignment outside the state machine. Verification
        // state stays at its Unverified default — no documents were collected.
        var supplier = new Supplier
        {
            Slug = await GenerateUniqueSlugAsync(draft.CompanyName, cancellationToken),
            DisplayName = Required(draft.CompanyName, 200),
            OrganizationId = organization.PublicId,
            Profile = new SupplierProfile
            {
                ContactPerson = Optional(draft.ContactPerson, 160),
                Phone = Optional(draft.Phone, 30),
                Email = Optional(draft.Email, 160),
                Website = Optional(draft.Website, 300),
            },
        };
        _db.Suppliers.Add(supplier);

        foreach (Guid categoryId in draft.CategoryIds.Distinct())
        {
            _db.SupplierCategories.Add(new SupplierCategory
            {
                SupplierId = supplier.PublicId,
                CategoryId = categoryId,
            });
        }

        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return supplier.PublicId;
    }

    /// <summary>
    /// A URL-stable, unique slug derived from the company name. Publication
    /// semantics (immutable after first publish) do not apply to a brand-new
    /// supplier, so a numeric suffix only ever disambiguates a collision at
    /// onboarding time.
    /// </summary>
    private async Task<string> GenerateUniqueSlugAsync(string companyName, CancellationToken cancellationToken)
    {
        string baseSlug = Required(Slugify(companyName), 150);
        if (baseSlug.Length == 0)
        {
            baseSlug = "supplier";
        }

        string candidate = baseSlug;
        for (int suffix = 2; ; suffix++)
        {
            bool exists = await _db.Suppliers.AnyAsync(s => s.Slug == candidate, cancellationToken);
            if (!exists)
            {
                return candidate;
            }

            candidate = Required($"{baseSlug}-{suffix}", 160);
        }
    }

    /// <summary>Lower-case ASCII slug; runs of non-alphanumerics collapse to one dash.</summary>
    private static string Slugify(string value)
    {
        string lowered = value.Trim().ToLowerInvariant();
        var builder = new StringBuilder(lowered.Length);
        bool lastWasDash = false;
        foreach (char character in lowered)
        {
            if (character is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                builder.Append(character);
                lastWasDash = false;
            }
            else if (!lastWasDash && builder.Length > 0)
            {
                builder.Append('-');
                lastWasDash = true;
            }
        }

        return builder.ToString().Trim('-');
    }

    /// <summary>Trim to a non-empty, length-bounded value for a required column.</summary>
    private static string Required(string value, int maxLength)
    {
        string trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    /// <summary>Trim to a length-bounded value, or null for a blank optional column.</summary>
    private static string? Optional(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
