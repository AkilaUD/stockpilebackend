using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Stockpile.Domain.Entities.Identity;
using Stockpile.Infrastructure.Persistence;

namespace Stockpile.Infrastructure.Identity;

/// <summary>
/// <c>IRoleStore&lt;Role&gt;</c> over the code-keyed <c>Roles</c> table
/// (domain-model §2.2: roles have no surrogate key — the code is the identity,
/// and there is no normalized-name column; lookups upper-case the code).
/// </summary>
public sealed class StockpileRoleStore : IRoleStore<Role>
{
    private readonly StockpileDbContext _db;

    public StockpileRoleStore(StockpileDbContext db) => _db = db;

    public async Task<IdentityResult> CreateAsync(Role role, CancellationToken cancellationToken)
    {
        _db.Roles.Add(role);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return IdentityResult.Success;
        }
        catch (DbUpdateException)
        {
            return IdentityResult.Failed(new IdentityError
            {
                Code = "DuplicateRoleName",
                Description = "A role with this code already exists.",
            });
        }
    }

    public async Task<IdentityResult> DeleteAsync(Role role, CancellationToken cancellationToken)
    {
        _db.Roles.Remove(role);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return IdentityResult.Success;
        }
        catch (DbUpdateException)
        {
            // Still referenced by RolePermission/OrganizationMember/UserRole rows.
            return IdentityResult.Failed(new IdentityError
            {
                Code = "RoleInUse",
                Description = "The role is still assigned and cannot be deleted.",
            });
        }
    }

    public Task<Role?> FindByIdAsync(string roleId, CancellationToken cancellationToken)
        => _db.Roles.FirstOrDefaultAsync(r => r.Code == roleId, cancellationToken);

    public Task<Role?> FindByNameAsync(string normalizedRoleName, CancellationToken cancellationToken)
#pragma warning disable CA1304, CA1311, CA1862 // server-side UPPER(): EF-translatable, ASCII codes (see StockpileUserStore role section)
        => _db.Roles.FirstOrDefaultAsync(
            r => r.Code.ToUpper() == normalizedRoleName,
            cancellationToken);
#pragma warning restore CA1304, CA1311, CA1862

    public Task<string?> GetNormalizedRoleNameAsync(Role role, CancellationToken cancellationToken)
        => Task.FromResult<string?>(role.Code.ToUpperInvariant());

    public Task<string> GetRoleIdAsync(Role role, CancellationToken cancellationToken)
        => Task.FromResult(role.Code);

    public Task<string?> GetRoleNameAsync(Role role, CancellationToken cancellationToken)
        => Task.FromResult<string?>(role.Code); // the code is the role's identity

    public Task SetNormalizedRoleNameAsync(Role role, string? normalizedName, CancellationToken cancellationToken)
        => Task.CompletedTask; // identity lives on Code; normalization is derived

    public Task SetRoleNameAsync(Role role, string? roleName, CancellationToken cancellationToken)
    {
        role.Code = roleName ?? string.Empty;
        return Task.CompletedTask;
    }

    public async Task<IdentityResult> UpdateAsync(Role role, CancellationToken cancellationToken)
    {
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return IdentityResult.Success;
        }
        catch (DbUpdateConcurrencyException)
        {
            return IdentityResult.Failed(new IdentityError
            {
                Code = "ConcurrencyFailure",
                Description = "The role record changed while it was being updated.",
            });
        }
    }

    public void Dispose() => GC.SuppressFinalize(this);
}
