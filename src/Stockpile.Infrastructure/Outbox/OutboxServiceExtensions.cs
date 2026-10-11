using Microsoft.Extensions.DependencyInjection;
using Stockpile.Application.Outbox;

namespace Stockpile.Infrastructure.Outbox;

/// <summary>
/// Total outbox wiring shared by Api and Worker (architecture §10.1). The
/// <see cref="OutboxSaveChangesInterceptor"/> is registered with persistence
/// (<see cref="Persistence.DatabaseStartupExtensions.AddStockpilePersistence"/>)
/// because it must see the real (scoped) context's SaveChanges; everything else
/// lives here. Consumers (<see cref="IOutboxConsumer"/>) register
/// themselves as their owning phases land; the registry starts empty.
/// </summary>
public static class OutboxServiceExtensions
{
    /// <summary>Registers the dispatch pipeline and the batch processor.</summary>
    public static IServiceCollection AddOutbox(this IServiceCollection services)
    {
        services.AddScoped<IOutboxEventDispatcher, RoutingOutboxEventDispatcher>();
        services.AddScoped<IOutboxProcessor, OutboxProcessor>();
        return services;
    }
}
