using Microsoft.Extensions.DependencyInjection;
using Stockpile.Application.Audit;

namespace Stockpile.Infrastructure.Audit;

/// <summary>
/// Audit + security-event wiring (task 1.29, security-test-matrix X7/X9/Z4).
/// Both services are scoped: they share the request's
/// <see cref="StockpileDbContext"/> so rows commit atomically with the change
/// they describe. Each HOST registers the ambient request context
/// (<see cref="ICurrentRequestContext"/> — HttpContext-backed in the API,
/// system defaults in the Worker); the burst monitor's SecurityEvent sink is
/// wired separately in the API Program (RateLimitBurstMonitor).
/// </summary>
public static class AuditServiceExtensions
{
    /// <summary>Registers <see cref="IAuditService"/> and <see cref="ISecurityEventService"/>.</summary>
    public static IServiceCollection AddAuditServices(this IServiceCollection services)
    {
        services.AddScoped<IAuditService, AuditService>();
        services.AddScoped<ISecurityEventService, SecurityEventService>();
        return services;
    }
}
