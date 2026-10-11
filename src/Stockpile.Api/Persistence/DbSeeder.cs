using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Stockpile.Contracts.Permissions;
using Stockpile.Domain.Entities.Identity;
using Stockpile.Domain.Entities.Platform;
using Stockpile.Domain.Entities.Reference;
using Stockpile.Domain.Enums;
using Stockpile.Domain.StateMachines;
using Stockpile.Infrastructure.Persistence;

namespace Stockpile.Api.Persistence;

/// <summary>
/// Idempotent bootstrap seed (implementation-plan 1.15/1.16), run at startup in
/// every environment — these rows are required platform data, not development
/// fixtures. Each pass converges the database to the declared truth:
/// permissions from <c>Contracts.Permissions</c>, transition rows from the
/// single source in <c>Domain.StateMachines.TransitionTables</c>, roles and
/// their grants from the <c>Seed:Roles</c> configuration (spec: roles are
/// seeded from configuration, never hard-coded in logic), and Sri Lanka
/// reference geography (domain-model §2.4). The Api is the only composition
/// layer that references Contracts, Domain, and the DbContext together.
/// </summary>
internal static class DbSeeder
{
    public static async Task SeedAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        using IServiceScope scope = services.CreateScope();
        StockpileDbContext db = scope.ServiceProvider.GetRequiredService<StockpileDbContext>();
        IConfiguration configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();

        await SeedPermissionsAsync(db, cancellationToken);
        await SeedStateTransitionsAsync(db, cancellationToken);
        await SeedRolesAsync(db, configuration, cancellationToken);
        await SeedGeographyAsync(db, cancellationToken);

        // Task 2E — the full live catalogue migration (177 categories, 2,322
        // products, 45 suppliers, 4,320 media assets) from the auditable harvest
        // record. Idempotent on the dataset hash; refuses to boot over a partial
        // or stale catalogue (see CatalogueSeeder).
        await CatalogueSeeder.SeedAsync(services, cancellationToken);

        // One SaveChanges = one transaction: the seed either fully applies or
        // leaves the database untouched.
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Upserts every code in <c>Contracts.Permissions.All</c> (the seed source
    /// for task 1.15). Rows are never deleted here: RolePermission and
    /// StateTransition reference them, and removing a permission from Contracts
    /// is a schema-level decision, not a startup one.
    /// </summary>
    private static async Task SeedPermissionsAsync(StockpileDbContext db, CancellationToken cancellationToken)
    {
        Dictionary<string, Permission> byCode = await db.Permissions
            .ToDictionaryAsync(p => p.Code, cancellationToken);

        foreach (PermissionDefinition definition in Permissions.All)
        {
            int dot = definition.Code.IndexOf('.');
            string category = dot > 0 ? definition.Code[..dot] : definition.Code;

            if (byCode.TryGetValue(definition.Code, out Permission? permission))
            {
                permission.Description = definition.Description;
                permission.Category = category;
            }
            else
            {
                db.Permissions.Add(new Permission
                {
                    Code = definition.Code,
                    Description = definition.Description,
                    Category = category,
                });
            }
        }
    }

    /// <summary>
    /// Mirrors every §11 machine into <c>StateTransition</c> (task 1.16), even
    /// for workflows whose handlers arrive in later phases — the admin UI and
    /// <c>StateMachine.Evaluate()</c> read the same rows. Also acts as a seed-
    /// time guard that every declared permission code exists in Contracts.
    /// </summary>
    private static async Task SeedStateTransitionsAsync(StockpileDbContext db, CancellationToken cancellationToken)
    {
        HashSet<string> permissionCodes = Permissions.All
            .Select(p => p.Code)
            .ToHashSet(StringComparer.Ordinal);

        Dictionary<(string Entity, string FromState, string ToState), StateTransition> byKey =
            await db.StateTransitions
                .ToDictionaryAsync(t => (t.Entity, t.FromState, t.ToState), cancellationToken);

        foreach ((string entity, StateTransitionRow row) in TransitionTables.AllMachines())
        {
            if (row.PermissionCode is not null && !permissionCodes.Contains(row.PermissionCode))
            {
                throw new InvalidOperationException(
                    $"State transition {entity} {row.From} -> {row.To} declares permission " +
                    $"'{row.PermissionCode}', which is not in Contracts.Permissions.All " +
                    "(domain-model §11 must stay within §2.2).");
            }

            if (byKey.TryGetValue((entity, row.From, row.To), out StateTransition? existing))
            {
                existing.PermissionCode = row.PermissionCode;
                existing.RequiresReason = row.RequiresReason;
                existing.IsForbidden = row.IsForbidden;
                existing.Notes = row.Notes;
            }
            else
            {
                db.StateTransitions.Add(new StateTransition
                {
                    Entity = entity,
                    FromState = row.From,
                    ToState = row.To,
                    PermissionCode = row.PermissionCode,
                    RequiresReason = row.RequiresReason,
                    IsForbidden = row.IsForbidden,
                    Notes = row.Notes,
                });
            }
        }
    }

    /// <summary>
    /// Loads roles and their grants from <c>Seed:Roles</c>. The configuration
    /// is the source of truth: grants missing from it are removed, and unknown
    /// permission codes or scopes fail startup with a precise message. Role rows
    /// present in the database but absent from config are left in place — they
    /// may be assigned to users, so retiring one is a deliberate operation.
    /// </summary>
    private static async Task SeedRolesAsync(
        StockpileDbContext db,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        List<SeedRoleDefinition> definitions =
            configuration.GetSection("Seed:Roles").Get<List<SeedRoleDefinition>>() ?? [];

        if (definitions.Count == 0)
        {
            throw new InvalidOperationException(
                "No roles configured under 'Seed:Roles'. Internal and organisation roles are seeded " +
                "from configuration (domain-model §2.2), never hard-coded — add them to appsettings.json.");
        }

        // Validated against Contracts rather than a DB query: the permission
        // upsert writes exactly this set in the same SaveChanges, so the two
        // cannot disagree here — and a config entry naming a retired permission
        // still fails fast.
        HashSet<string> permissionCodes = Permissions.All
            .Select(p => p.Code)
            .ToHashSet(StringComparer.Ordinal);

        // ---- Validate the whole configuration before touching any row ----
        HashSet<string> seenCodes = new(StringComparer.Ordinal);
        foreach (SeedRoleDefinition definition in definitions)
        {
            if (!seenCodes.Add(definition.Code))
            {
                throw new InvalidOperationException($"Duplicate role code '{definition.Code}' in Seed:Roles.");
            }

            if (!Enum.TryParse<RoleScope>(definition.Scope, ignoreCase: true, out _))
            {
                throw new InvalidOperationException(
                    $"Role '{definition.Code}' has unknown scope '{definition.Scope}' " +
                    "(expected Platform | Organization | Supplier).");
            }

            foreach (string code in definition.Permissions)
            {
                if (!permissionCodes.Contains(code))
                {
                    throw new InvalidOperationException(
                        $"Role '{definition.Code}' grants unknown permission '{code}'. " +
                        "Permission codes come from Contracts.Permissions.All.");
                }
            }
        }

        // ---- Converge roles and grants ----
        Dictionary<string, Role> rolesByCode = await db.Roles
            .ToDictionaryAsync(r => r.Code, cancellationToken);

        Dictionary<string, List<RolePermission>> grantsByRole = (await db.RolePermissions
                .ToListAsync(cancellationToken))
            .GroupBy(g => g.RoleCode, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        foreach (SeedRoleDefinition definition in definitions)
        {
            Enum.TryParse<RoleScope>(definition.Scope, ignoreCase: true, out RoleScope scope);

            if (rolesByCode.TryGetValue(definition.Code, out Role? role))
            {
                role.Name = definition.Name;
                role.Scope = scope;
            }
            else
            {
                role = new Role { Code = definition.Code, Name = definition.Name, Scope = scope };
                db.Roles.Add(role);
                rolesByCode[definition.Code] = role;
            }

            List<RolePermission> currentGrants =
                grantsByRole.TryGetValue(definition.Code, out List<RolePermission>? grants)
                    ? grants
                    : [];

            HashSet<string> currentCodes = currentGrants
                .Select(g => g.PermissionCode)
                .ToHashSet(StringComparer.Ordinal);
            HashSet<string> wantedCodes = definition.Permissions
                .ToHashSet(StringComparer.Ordinal);

            foreach (string code in wantedCodes)
            {
                if (!currentCodes.Contains(code))
                {
                    db.RolePermissions.Add(new RolePermission
                    {
                        RoleCode = definition.Code,
                        PermissionCode = code,
                    });
                }
            }

            foreach (RolePermission stale in currentGrants)
            {
                if (!wantedCodes.Contains(stale.PermissionCode))
                {
                    db.RolePermissions.Remove(stale);
                }
            }
        }
    }

    /// <summary>
    /// Sri Lanka reference geography (domain-model §2.4): code-keyed, never
    /// free text at the point of use. Country seeds <c>LK</c> only in Phase 1;
    /// the full ISO 3166-1 list arrives with the Phase 2 catalogue task, so no
    /// speculative rows are written here.
    /// </summary>
    private static async Task SeedGeographyAsync(StockpileDbContext db, CancellationToken cancellationToken)
    {
        if (!await db.Countries.AnyAsync(c => c.Code == "LK", cancellationToken))
        {
            db.Countries.Add(new Country { Code = "LK", Name = "Sri Lanka", NumericCode = "144" });
        }

        Dictionary<string, Province> provinces = await db.Provinces
            .ToDictionaryAsync(p => p.Code, cancellationToken);

        foreach ((string code, string name) in Provinces)
        {
            if (provinces.TryGetValue(code, out Province? province))
            {
                province.Name = name;
            }
            else
            {
                db.Provinces.Add(new Province { Code = code, Name = name });
            }
        }

        Dictionary<string, District> districts = await db.Districts
            .ToDictionaryAsync(d => d.Code, cancellationToken);

        foreach ((string code, string name, string provinceCode) in Districts)
        {
            if (districts.TryGetValue(code, out District? district))
            {
                district.Name = name;
                district.ProvinceCode = provinceCode;
            }
            else
            {
                db.Districts.Add(new District
                {
                    Code = code,
                    Name = name,
                    ProvinceCode = provinceCode,
                });
            }
        }
    }

    private static readonly (string Code, string Name)[] Provinces =
    [
        ("western", "Western"),
        ("central", "Central"),
        ("southern", "Southern"),
        ("northern", "Northern"),
        ("eastern", "Eastern"),
        ("north-western", "North Western"),
        ("north-central", "North Central"),
        ("uva", "Uva"),
        ("sabaragamuwa", "Sabaragamuwa"),
    ];

    private static readonly (string Code, string Name, string ProvinceCode)[] Districts =
    [
        ("colombo", "Colombo", "western"),
        ("gampaha", "Gampaha", "western"),
        ("kalutara", "Kalutara", "western"),
        ("kandy", "Kandy", "central"),
        ("matale", "Matale", "central"),
        ("nuwara-eliya", "Nuwara Eliya", "central"),
        ("galle", "Galle", "southern"),
        ("matara", "Matara", "southern"),
        ("hambantota", "Hambantota", "southern"),
        ("jaffna", "Jaffna", "northern"),
        ("kilinochchi", "Kilinochchi", "northern"),
        ("mannar", "Mannar", "northern"),
        ("mullaitivu", "Mullaitivu", "northern"),
        ("vavuniya", "Vavuniya", "northern"),
        ("batticaloa", "Batticaloa", "eastern"),
        ("ampara", "Ampara", "eastern"),
        ("trincomalee", "Trincomalee", "eastern"),
        ("kurunegala", "Kurunegala", "north-western"),
        ("puttalam", "Puttalam", "north-western"),
        ("anuradhapura", "Anuradhapura", "north-central"),
        ("polonnaruwa", "Polonnaruwa", "north-central"),
        ("badulla", "Badulla", "uva"),
        ("monaragala", "Monaragala", "uva"),
        ("ratnapura", "Ratnapura", "sabaragamuwa"),
        ("kegalle", "Kegalle", "sabaragamuwa"),
    ];
}

/// <summary>One row of the <c>Seed:Roles</c> configuration section.</summary>
internal sealed class SeedRoleDefinition
{
    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>Platform | Organization | Supplier.</summary>
    public string Scope { get; set; } = string.Empty;

    public string[] Permissions { get; set; } = [];
}
