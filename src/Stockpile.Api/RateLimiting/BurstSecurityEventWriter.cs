using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Stockpile.Application.Audit;
using Stockpile.Domain.Enums;

namespace Stockpile.Api.RateLimiting;

/// <summary>
/// Bridges the burst monitor's detection to <see cref="ISecurityEventService"/>
/// (task 1.29): a crossed burst becomes a <c>RateLimitHit</c> security event
/// with a short human detail and structured metadata (policy, partition,
/// threshold, window) — never a header, token, or payload (Z4). Storage
/// failures are swallowed and logged: the rejection response (429) must never
/// be degraded by failing audit persistence, and the monitor's own Warning log
/// (EventId 2200) remains the always-present signal.
/// </summary>
internal static class BurstSecurityEventWriter
{
    public static Func<RateLimitBurstMonitor.BurstSignal, CancellationToken, Task> For(
        IServiceProvider requestServices) =>
        async (signal, cancellationToken) =>
        {
            using IServiceScope scope = requestServices.CreateScope();
            try
            {
                var securityEvents = scope.ServiceProvider.GetRequiredService<ISecurityEventService>();
                await securityEvents.RecordAsync(
                    SecurityEventType.RateLimitHit,
                    detail: $"Rate-limit burst on policy '{signal.Policy}' — "
                        + $"{signal.BurstThreshold} rejections within {signal.BurstWindowSeconds}s",
                    metadata: JsonSerializer.Serialize(
                        new
                        {
                            signal.Policy,
                            signal.Partition,
                            signal.BurstThreshold,
                            signal.BurstWindowSeconds,
                        },
                        JsonSerializerOptions.Web),
                    cancellationToken: cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                ILogger? logger = scope.ServiceProvider
                    .GetService<ILoggerFactory>()
                    ?.CreateLogger("Stockpile.Api.RateLimiting.BurstSecurityEvents");
                if (logger is not null)
                {
                    BurstSecurityEventLog.PersistFailed(logger, exception, signal.Policy);
                }
            }
        };
}
