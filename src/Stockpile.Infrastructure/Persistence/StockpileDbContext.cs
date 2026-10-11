using Microsoft.EntityFrameworkCore;
using Stockpile.Domain.Common;
using Stockpile.Domain.Entities.Catalogue;
using Stockpile.Domain.Entities.Identity;
using Stockpile.Domain.Entities.Media;
using Stockpile.Domain.Entities.Platform;
using Stockpile.Domain.Entities.Reference;

namespace Stockpile.Infrastructure.Persistence;

/// <summary>
/// The application database (architecture §9.1). Fluent configurations live in
/// <c>Persistence/Configurations</c>, one class per aggregate; migrations are
/// source-controlled and never auto-applied outside Development (§9.2).
/// This context deliberately does not derive from <c>IdentityDbContext</c>:
/// Identity is integrated through custom stores over these same DbSets, so the
/// schema stays exactly as <c>domain-model.md</c> specifies it.
/// </summary>
public sealed class StockpileDbContext : DbContext
{
    public StockpileDbContext(DbContextOptions<StockpileDbContext> options)
        : base(options)
    {
    }

    // ---- Identity & organizations (domain-model §2) ----
    public DbSet<User> Users => Set<User>();

    public DbSet<Role> Roles => Set<Role>();

    public DbSet<Permission> Permissions => Set<Permission>();

    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();

    public DbSet<UserRole> UserRoles => Set<UserRole>();

    public DbSet<Organization> Organizations => Set<Organization>();

    public DbSet<OrganizationMember> OrganizationMembers => Set<OrganizationMember>();

    public DbSet<Address> Addresses => Set<Address>();

    public DbSet<UserPreference> UserPreferences => Set<UserPreference>();

    public DbSet<NotificationPreference> NotificationPreferences => Set<NotificationPreference>();

    public DbSet<LoginSession> LoginSessions => Set<LoginSession>();

    public DbSet<RefreshSession> RefreshSessions => Set<RefreshSession>();

    public DbSet<PasswordResetToken> PasswordResetTokens => Set<PasswordResetToken>();

    public DbSet<EmailVerificationToken> EmailVerificationTokens => Set<EmailVerificationToken>();

    // ---- Platform / security / operations (domain-model §10) ----
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    public DbSet<SecurityEvent> SecurityEvents => Set<SecurityEvent>();

    public DbSet<IdempotencyKey> IdempotencyKeys => Set<IdempotencyKey>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public DbSet<StateTransition> StateTransitions => Set<StateTransition>();

    public DbSet<SystemSetting> SystemSettings => Set<SystemSetting>();

    public DbSet<FeatureFlag> FeatureFlags => Set<FeatureFlag>();

    // ---- Media (§4.7) & reference data (§2.4/§4.6) ----
    public DbSet<MediaAsset> MediaAssets => Set<MediaAsset>();

    public DbSet<Country> Countries => Set<Country>();

    public DbSet<Province> Provinces => Set<Province>();

    public DbSet<District> Districts => Set<District>();

    // ---- Catalogue (domain-model §3/§4) ----
    public DbSet<Category> Categories => Set<Category>();

    public DbSet<AttributeDefinition> AttributeDefinitions => Set<AttributeDefinition>();

    public DbSet<AttributeOption> AttributeOptions => Set<AttributeOption>();

    public DbSet<CategoryAttributeDefinition> CategoryAttributeDefinitions => Set<CategoryAttributeDefinition>();

    public DbSet<Product> Products => Set<Product>();

    public DbSet<ProductAttributeValue> ProductAttributeValues => Set<ProductAttributeValue>();

    public DbSet<ProductPrice> ProductPrices => Set<ProductPrice>();

    public DbSet<ProductPriceHistory> ProductPriceHistory => Set<ProductPriceHistory>();

    public DbSet<ProductStock> ProductStocks => Set<ProductStock>();

    public DbSet<StockAdjustment> StockAdjustments => Set<StockAdjustment>();

    public DbSet<ProductMedia> ProductMedias => Set<ProductMedia>();

    public DbSet<Brand> Brands => Set<Brand>();

    public DbSet<Manufacturer> Manufacturers => Set<Manufacturer>();

    public DbSet<UnitOfMeasure> UnitOfMeasures => Set<UnitOfMeasure>();

    public DbSet<Supplier> Suppliers => Set<Supplier>();

    public DbSet<SupplierProfile> SupplierProfiles => Set<SupplierProfile>();

    public DbSet<SupplierCategory> SupplierCategories => Set<SupplierCategory>();

    public DbSet<SupplierAudiencePolicy> SupplierAudiencePolicies => Set<SupplierAudiencePolicy>();

    public DbSet<SupplierServiceArea> SupplierServiceAreas => Set<SupplierServiceArea>();

    public DbSet<SupplierVerification> SupplierVerifications => Set<SupplierVerification>();

    public DbSet<SupplierBadge> SupplierBadges => Set<SupplierBadge>();

    public DbSet<SupplierPublicPageConfig> SupplierPublicPageConfigs => Set<SupplierPublicPageConfig>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Domain events are outbox transport (architecture §10.1): the interceptor
        // materialises them as OutboxMessage rows; the event record itself is never
        // a persisted entity, so the Events collection on event-raising aggregates
        // must not be discovered as a navigation.
        modelBuilder.Ignore<DomainEvent>();

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(StockpileDbContext).Assembly);
    }
}
