using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Stockpile.Domain.Entities.Identity;
using Stockpile.Infrastructure.Persistence;

namespace Stockpile.Infrastructure.Identity;

/// <summary>
/// ASP.NET Core Identity stores over our own schema (task 1.12). The domain
/// model's <c>User</c> is a plain entity — email is the login identifier
/// (<c>EmailNormalized</c> is the unique key, and there is no UserName column),
/// <c>EmailConfirmedAt</c> is a timestamp rather than Identity's bool, and
/// roles are natural-keyed codes. Adapting here keeps
/// <c>domain-model.md</c> as the single schema truth while
/// <c>UserManager</c>/<c>SignInManager</c> keep their battle-tested password,
/// lockout, and validation logic.
/// </summary>
public sealed class StockpileUserStore :
    IUserStore<User>,
    IUserEmailStore<User>,
    IUserPasswordStore<User>,
    IUserLockoutStore<User>,
    IUserTwoFactorStore<User>,
    IUserRoleStore<User>
{
    private readonly StockpileDbContext _db;

    public StockpileUserStore(StockpileDbContext db) => _db = db;

    // ---- IUserStore<User> ----

    public async Task<IdentityResult> CreateAsync(User user, CancellationToken cancellationToken)
    {
        _db.Users.Add(user);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return IdentityResult.Success;
        }
        catch (DbUpdateException)
        {
            // Unique index (EmailNormalized) or PK collision — surfaced as a
            // field error by the registration handler, never as a stack trace.
            return IdentityResult.Failed(new IdentityError
            {
                Code = "DuplicateUserName",
                Description = "An account with this email already exists.",
            });
        }
    }

    public async Task<IdentityResult> DeleteAsync(User user, CancellationToken cancellationToken)
    {
        _db.Users.Remove(user);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return IdentityResult.Success;
        }
        catch (DbUpdateException)
        {
            return IdentityResult.Failed(new IdentityError { Code = "DeleteFailed", Description = "The user could not be deleted." });
        }
    }

    public Task<User?> FindByIdAsync(string userId, CancellationToken cancellationToken)
        => Guid.TryParse(userId, out Guid id)
            ? _db.Users.FirstOrDefaultAsync(u => u.PublicId == id, cancellationToken)
            : Task.FromResult<User?>(null);

    public Task<User?> FindByNameAsync(string normalizedUserName, CancellationToken cancellationToken)
        => _db.Users.FirstOrDefaultAsync(u => u.EmailNormalized == normalizedUserName, cancellationToken);

    public Task<string?> GetNormalizedUserNameAsync(User user, CancellationToken cancellationToken)
        => Task.FromResult<string?>(user.EmailNormalized);

    public Task<string?> GetUserNameAsync(User user, CancellationToken cancellationToken)
        => Task.FromResult<string?>(user.Email);

    public Task SetUserNameAsync(User user, string? userName, CancellationToken cancellationToken)
    {
        user.Email = userName ?? string.Empty;
        return Task.CompletedTask;
    }

    public Task SetNormalizedUserNameAsync(User user, string? normalizedName, CancellationToken cancellationToken)
    {
        // No UserName column: the normalized name IS the normalized email (§2.1).
        user.EmailNormalized = normalizedName ?? string.Empty;
        return Task.CompletedTask;
    }

    public async Task<IdentityResult> UpdateAsync(User user, CancellationToken cancellationToken)
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
                Description = "The user record changed while it was being updated.",
            });
        }
    }

    // ---- IUserEmailStore<User> ----

    public Task<string?> GetEmailAsync(User user, CancellationToken cancellationToken)
        => Task.FromResult<string?>(user.Email);

    public Task SetEmailAsync(User user, string? email, CancellationToken cancellationToken)
    {
        user.Email = email ?? string.Empty;
        return Task.CompletedTask;
    }

    public Task<bool> GetEmailConfirmedAsync(User user, CancellationToken cancellationToken)
        => Task.FromResult(user.EmailConfirmedAt is not null);

    public Task SetEmailConfirmedAsync(User user, bool confirmed, CancellationToken cancellationToken)
    {
        user.EmailConfirmedAt = confirmed ? DateTime.UtcNow : null;
        return Task.CompletedTask;
    }

    public Task<User?> FindByEmailAsync(string normalizedEmail, CancellationToken cancellationToken)
        => _db.Users.FirstOrDefaultAsync(u => u.EmailNormalized == normalizedEmail, cancellationToken);

    public Task<string?> GetNormalizedEmailAsync(User user, CancellationToken cancellationToken)
        => Task.FromResult<string?>(user.EmailNormalized);

    public Task SetNormalizedEmailAsync(User user, string? normalizedEmail, CancellationToken cancellationToken)
    {
        user.EmailNormalized = normalizedEmail ?? string.Empty;
        return Task.CompletedTask;
    }

    // ---- IUserPasswordStore<User> ----

    public Task<string?> GetPasswordHashAsync(User user, CancellationToken cancellationToken)
        => Task.FromResult<string?>(user.PasswordHash);

    public Task SetPasswordHashAsync(User user, string? passwordHash, CancellationToken cancellationToken)
    {
        user.PasswordHash = passwordHash ?? string.Empty;
        return Task.CompletedTask;
    }

    public Task<bool> HasPasswordAsync(User user, CancellationToken cancellationToken)
        => Task.FromResult(!string.IsNullOrEmpty(user.PasswordHash));

    // ---- IUserLockoutStore<User> ----

    public Task<DateTimeOffset?> GetLockoutEndDateAsync(User user, CancellationToken cancellationToken)
        => Task.FromResult<DateTimeOffset?>(
            user.LockoutEndsAt is DateTime endsAt
                ? new DateTimeOffset(DateTime.SpecifyKind(endsAt, DateTimeKind.Utc))
                : null);

    public Task SetLockoutEndDateAsync(User user, DateTimeOffset? lockoutEnd, CancellationToken cancellationToken)
    {
        user.LockoutEndsAt = lockoutEnd?.UtcDateTime; // datetime2(7) UTC only (§1)
        return Task.CompletedTask;
    }

    public Task<int> IncrementAccessFailedCountAsync(User user, CancellationToken cancellationToken)
    {
        user.FailedAccessCount++;
        return Task.FromResult(user.FailedAccessCount);
    }

    public Task ResetAccessFailedCountAsync(User user, CancellationToken cancellationToken)
    {
        user.FailedAccessCount = 0;
        return Task.CompletedTask;
    }

    public Task<int> GetAccessFailedCountAsync(User user, CancellationToken cancellationToken)
        => Task.FromResult(user.FailedAccessCount);

    public Task<bool> GetLockoutEnabledAsync(User user, CancellationToken cancellationToken)
        => Task.FromResult(true); // lockout is always policy-driven; there is no per-user opt-out

    public Task SetLockoutEnabledAsync(User user, bool enabled, CancellationToken cancellationToken)
        => Task.CompletedTask;

    // ---- IUserTwoFactorStore<User> ----

    public Task<bool> GetTwoFactorEnabledAsync(User user, CancellationToken cancellationToken)
        => Task.FromResult(user.TwoFactorEnabled);

    public Task SetTwoFactorEnabledAsync(User user, bool enabled, CancellationToken cancellationToken)
    {
        user.TwoFactorEnabled = enabled;
        return Task.CompletedTask;
    }

    // ---- IUserRoleStore<User> ----
    // CA1304/CA1311/CA1862: these comparisons run server-side only. EF translates
    // string.ToUpper() to SQL UPPER() (database collation, never a .NET culture),
    // and role codes are ASCII — the culture-sensitivity the rules guard against
    // cannot occur here. There is no EF-translatable OrdinalIgnoreCase overload
    // we can rely on across providers.
#pragma warning disable CA1304, CA1311, CA1862

    public async Task AddToRoleAsync(User user, string roleName, CancellationToken cancellationToken)
    {
        string normalized = roleName.ToUpperInvariant();
        Role? role = await _db.Roles
            .FirstOrDefaultAsync(r => r.Code.ToUpper() == normalized, cancellationToken)
            ?? throw new InvalidOperationException($"Role '{roleName}' does not exist.");

        bool exists = await _db.UserRoles.AnyAsync(
            ur => ur.UserId == user.PublicId && ur.RoleCode == role.Code,
            cancellationToken);
        if (!exists)
        {
            _db.UserRoles.Add(new UserRole { UserId = user.PublicId, RoleCode = role.Code });
            await _db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task RemoveFromRoleAsync(User user, string roleName, CancellationToken cancellationToken)
    {
        string normalized = roleName.ToUpperInvariant();
        UserRole? row = await _db.UserRoles
            .FirstOrDefaultAsync(
                ur => ur.UserId == user.PublicId && ur.RoleCode.ToUpper() == normalized,
                cancellationToken);

        if (row is not null)
        {
            _db.UserRoles.Remove(row);
            await _db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<IList<string>> GetRolesAsync(User user, CancellationToken cancellationToken)
        => await _db.UserRoles
            .Where(ur => ur.UserId == user.PublicId)
            .Select(ur => ur.RoleCode)
            .ToListAsync(cancellationToken);

    public async Task<IList<User>> GetUsersInRoleAsync(string roleName, CancellationToken cancellationToken)
    {
        string normalized = roleName.ToUpperInvariant();
        return await _db.UserRoles
            .Where(ur => ur.RoleCode.ToUpper() == normalized)
            .Select(ur => ur.UserId)
            .Join(_db.Users, userId => userId, u => u.PublicId, (_, u) => u)
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> IsInRoleAsync(User user, string roleName, CancellationToken cancellationToken)
    {
        string normalized = roleName.ToUpperInvariant();
        return await _db.UserRoles.AnyAsync(
            ur => ur.UserId == user.PublicId && ur.RoleCode.ToUpper() == normalized,
            cancellationToken);
    }
#pragma warning restore CA1304, CA1311, CA1862

    public Task<string> GetUserIdAsync(User user, CancellationToken cancellationToken)
        => Task.FromResult(user.PublicId.ToString());

    public void Dispose() => GC.SuppressFinalize(this);
}
