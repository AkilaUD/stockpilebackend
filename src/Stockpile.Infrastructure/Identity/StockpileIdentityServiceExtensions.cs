using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Stockpile.Application.Authentication;
using Stockpile.Domain.Entities.Identity;
using Stockpile.Infrastructure.Identity;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Identity wiring for the Stockpile schema (task 1.12): our stores, core
/// UserManager, role support, and a role-aware claims factory. Password policy
/// (threat-model §4: minimum length from config, no composition theatre), the
/// lockout base window (task 1.36; exponential backoff lands 1.39), and token
/// providers (verification/reset, 1.37/1.38) are configured here. Cookie
/// scheme and antiforgery are wired in Phase 1D (tasks 1.34–1.36) in the Api
/// host; this registration is the identity half. Registered before
/// <c>AddIdentityCore</c> so its TryAdd calls find ours first.
/// </summary>
public static class StockpileIdentityServiceExtensions
{
    public static IServiceCollection AddStockpileIdentity(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<IUserStore<User>, StockpileUserStore>();
        services.AddScoped<IRoleStore<Role>, StockpileRoleStore>();

        // Role claims on the principal: the default factory omits them, and the
        // permission policy provider (1.42) reads them from the principal.
        // (The framework ships a single-argument IUserClaimsPrincipalFactory<TUser>;
        // the role-aware implementation is the two-generic UserClaimsPrincipalFactory.)
        services.AddScoped<IUserClaimsPrincipalFactory<User>, UserClaimsPrincipalFactory<User, Role>>();

        services
            .AddIdentityCore<User>()
            .AddRoles<Role>()
            // Verification/reset tokens. Reset tokens are NOT the framework's
            // stateless DataProtector provider: the threat model demands
            // single-use + "hashed at rest" (T1.5), and a stateless blob cannot
            // enforce single-use (the .NET 10 DataProtectorTokenProvider happily
            // re-validates a consumed token). The reset token is therefore an
            // opaque 256-bit value with its SHA-256 hash stored in a row that is
            // consumed on use (task 1.37, A5/A6), exactly like RefreshSession.
            // The two-factor providers below resolve statelessly and stay
            // framework-only; email verification (1.38) uses EmailTokenProvider.
            .AddTokenProvider<EmailTokenProvider<User>>(TokenOptions.DefaultEmailProvider)
            .AddTokenProvider<PhoneNumberTokenProvider<User>>(TokenOptions.DefaultPhoneProvider)
            .AddTokenProvider<AuthenticatorTokenProvider<User>>(TokenOptions.DefaultAuthenticatorProvider);

        // Threat-model §4: "≥ 10 chars, breached-password check hook, no
        // composition theatre" — length is the ONLY composition rule, so the
        // complexity flags are switched off. Lockout base window from
        // Auth:LockoutBaseMinutes powers Identity's own counter plumbing
        // (UserManager.AccessFailedAsync); the exponential backoff + jitter of
        // task 1.39 replaces the fixed window while keeping this counter.
        PasswordPolicyOptions passwordPolicy =
            configuration.GetSection(PasswordPolicyOptions.SectionName)
                .Get<PasswordPolicyOptions>() ?? new PasswordPolicyOptions();
        AuthOptions authOptions =
            configuration.GetSection(AuthOptions.SectionName)
                .Get<AuthOptions>() ?? new AuthOptions();

        // Threat-model T1.5 "short TTL": a reset token dies after this many minutes
        // (the persisted row's ExpiresAtUtc, task 1.37 A6). Refuse to boot on a
        // nonsense window — a reset flow whose tokens never expire (or die
        // instantly) is a security bug, not a configuration choice (the Testing
        // host uses a sub-minute value so the expiry integration test drives the
        // real row expiry).
        if (authOptions.PasswordResetTokenMinutes is < 0.1 or > 1440)
        {
            throw new InvalidOperationException(
                $"Auth:PasswordResetTokenMinutes must be inside [0.1, 1440] minutes (threat-model T1.5 short reset-token TTL). "
                + $"Configured: {authOptions.PasswordResetTokenMinutes}.");
        }

        // Task 1.38 — the verification-link window (the row's ExpiresAtUtc). A
        // flow whose verification links never expire (or die instantly) is also a
        // bug, never a configuration choice; the Testing host uses a sub-minute
        // value so the expiry integration test drives the real row expiry.
        if (authOptions.EmailVerificationTokenMinutes is < 0.1 or > 10080)
        {
            throw new InvalidOperationException(
                $"Auth:EmailVerificationTokenMinutes must be inside [0.1, 10080] minutes (email-verification link window, task 1.38). "
                + $"Configured: {authOptions.EmailVerificationTokenMinutes}.");
        }

        // Task 1.40 — the refresh-token window (threat-model T1.4). A refresh
        // token that never expires (or dies instantly) is a bug, never a
        // configuration choice; the Testing host uses a sub-minute value so the
        // expiry integration test drives the real persisted row.
        if (authOptions.RefreshTokenMinutes is < 0.1 or > 10080)
        {
            throw new InvalidOperationException(
                $"Auth:RefreshTokenMinutes must be inside [0.1, 10080] minutes (refresh-token TTL, task 1.40). "
                + $"Configured: {authOptions.RefreshTokenMinutes}.");
        }

        // Task 1.39 — the exponential lockout ladder (threat-model T1.1): a base
        // below 6 s is nonsense, the ceiling must never exceed a week, and the cap
        // must sit at or above the base or the "exponential" part is flat. The
        // Testing host uses a sub-minute base so the episode-growth test drives
        // the real persisted window.
        if (authOptions.LockoutBaseMinutes is < 0.1 or > 1440)
        {
            throw new InvalidOperationException(
                $"Auth:LockoutBaseMinutes must be inside [0.1, 1440] minutes (lockout backoff base, task 1.39). "
                + $"Configured: {authOptions.LockoutBaseMinutes}.");
        }

        if (authOptions.LockoutMaxMinutes < authOptions.LockoutBaseMinutes || authOptions.LockoutMaxMinutes > 10080)
        {
            throw new InvalidOperationException(
                $"Auth:LockoutMaxMinutes must sit inside [LockoutBaseMinutes, 10080] (the backoff ladder's ceiling, never below its base). "
                + $"Configured: base {authOptions.LockoutBaseMinutes}, max {authOptions.LockoutMaxMinutes}.");
        }

        services.Configure<IdentityOptions>(options =>
        {
            options.Password.RequiredLength = Math.Max(1, passwordPolicy.MinimumLength);
            options.Password.RequiredUniqueChars = 1;
            options.Password.RequireNonAlphanumeric = false;
            options.Password.RequireDigit = false;
            options.Password.RequireLowercase = false;
            options.Password.RequireUppercase = false;

            options.Lockout.MaxFailedAccessAttempts = 5;
            // Base for Identity's own counter plumbing (the store write that
            // flags the 5th failure); AuthenticationService overrides the end with
            // the task 1.39 exponential window in the same request.
            options.Lockout.DefaultLockoutTimeSpan =
                TimeSpan.FromMinutes(Math.Max(1, authOptions.LockoutBaseMinutes));
            options.Lockout.AllowedForNewUsers = true;
        });

        return services;
    }
}