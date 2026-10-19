using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Stockpile.Application.Authentication;
using Stockpile.Application.Idempotency;
using Stockpile.Application.Organizations;
using Stockpile.Application.Procurement;
using Stockpile.Application.Registration;
using Stockpile.Application.Suppliers;
using Stockpile.Infrastructure.Authentication;
using Stockpile.Infrastructure.Idempotency;
using Stockpile.Infrastructure.Outbox;
using Stockpile.Infrastructure.Persistence.Stores;

namespace Stockpile.Infrastructure.Persistence;

/// <summary>
/// Persistence startup wiring shared by Api and Worker (architecture §9.2):
/// opt-in auto-migrate that is refused outright in Production, plus the
/// idempotent migration step both hosts may call.
/// </summary>
public static class DatabaseStartupExtensions
{
    /// <summary>Registers <see cref="StockpileDbContext"/> against SQL Server. No retrying execution strategy: the platform uses explicit user-initiated transactions (§9.3), which a retry strategy must wrap or fail on.</summary>
    public static IServiceCollection AddStockpilePersistence(this IServiceCollection services, string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Connection string 'Stockpile' is required but not configured.");
        }

        // Task 1.28 — the outbox interceptor is part of persistence: it must see
        // the real scoped context's SaveChanges to write OutboxMessage rows in
        // the same transaction as aggregate changes (architecture §10.1).
        services.TryAddSingleton<OutboxSaveChangesInterceptor>();
        services.AddDbContext<StockpileDbContext>((sp, options) => options
            .UseSqlServer(connectionString)
            .AddInterceptors(sp.GetRequiredService<OutboxSaveChangesInterceptor>()));

        // Task 1.27 — idempotency port + a single clock for retention windows
        // (the outbox worker will reuse the same TimeProvider).
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IIdempotencyStore, IdempotencyStore>();

        // Task 1.36 — server-side session rows + permission resolution over the
        // same scoped context the cookie hook and identity payload run in.
        services.AddScoped<ILoginSessionStore, LoginSessionStore>();
        services.AddScoped<IUserPermissionStore, UserPermissionStore>();

        // Task 1.37 — password-reset tokens: hashed at rest, single-use, short
        // TTL (threat-model T1.5, security-test-matrix A5/A6).
        services.AddScoped<IPasswordResetTokenStore, PasswordResetTokenStore>();

        // Task 1.38 — email-verification tokens: the same ledger discipline,
        // probed by hash alone from the anonymous verify endpoint (A11).
        services.AddScoped<IEmailVerificationTokenStore, EmailVerificationTokenStore>();

        // business-flows §2/§3 — resumable registration drafts (api.md §2.1): the
        // same hashed-at-rest discipline as the ledgers above, addressed by hash
        // alone from the anonymous save/resume endpoints.
        services.AddScoped<IRegistrationDraftStore, RegistrationDraftStore>();

        // Task 1.40 — rotating refresh tokens: hashed at rest, chain-linked by
        // RotatedToId, reuse of a rotated head revokes the whole chain (T1.4,
        // A8/A9).
        services.AddScoped<IRefreshSessionStore, RefreshSessionStore>();

        // Task 1.43 — organisations, members and addresses (implementation-plan
        // 1.43, P9): one scoped store for the use cases and the session payload.
        services.AddScoped<IOrganizationStore, OrganizationStore>();

        // Task 2F — supplier self-registration: one transactional write that
        // creates the supplier company, its membership and its category coverage.
        services.AddScoped<ISupplierRegistrationStore, SupplierRegistrationStore>();

        // Procurement — RFQ persistence for the draft/submit/matching use cases
        // (domain-model §5.3–§5.5): writes commit the aggregate, its matches and
        // its status history together.
        services.AddScoped<IRfqStore, RfqStore>();

        // Procurement — the supplier's side of the same rows: the buying-lead
        // inbox over RFQMatches (business-flows §6, api.md §2.9).
        services.AddScoped<ISupplierLeadsStore, SupplierLeadsStore>();

        // Procurement — quotations (domain-model §6.1–§6.2): the quote aggregate
        // with its append-only revisions, committed together with the lead's own
        // response.
        services.AddScoped<IQuotesStore, QuotesStore>();

        return services;
    }

    /// <summary>
    /// Applies pending migrations when — and only when — the
    /// <c>Database:AutoMigrate</c> opt-in flag is set. The flag is a hard error
    /// in Production: there, migrations are applied by the deployment step
    /// (architecture §9.2, security-test-matrix X3).
    /// </summary>
    public static async Task MigrateDatabaseIfEnabledAsync(
        this IServiceProvider services,
        bool autoMigrateEnabled,
        bool isProduction,
        CancellationToken cancellationToken = default)
    {
        if (!autoMigrateEnabled)
        {
            return;
        }

        if (isProduction)
        {
            throw new InvalidOperationException(
                "Database:AutoMigrate must never be enabled in Production. " +
                "Migrations are applied by the deployment pipeline (architecture §9.2).");
        }

        await using AsyncServiceScope scope = services.CreateAsyncScope();
        StockpileDbContext db = scope.ServiceProvider.GetRequiredService<StockpileDbContext>();
        await db.Database.MigrateAsync(cancellationToken);
    }
}
