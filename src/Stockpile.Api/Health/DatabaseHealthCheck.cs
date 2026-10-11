using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Stockpile.Infrastructure.Persistence;

namespace Stockpile.Api.Health;

/// <summary>
/// Task 1.24 — readiness dependency probe: SQL Server reachable within a short
/// timeout so a failed probe answers fast instead of hanging the health
/// endpoint. Failures are logged server-side but reported to the caller as a
/// bare unhealthy status — <c>/api/v1/health/ready</c> is unauthenticated and
/// must not leak exceptions, connection strings, or server names
/// (security-threat-model T4.5, Z4).
/// </summary>
public sealed partial class DatabaseHealthCheck(
    StockpileDbContext dbContext,
    ILogger<DatabaseHealthCheck> logger) : IHealthCheck
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);

        try
        {
            if (await dbContext.Database.CanConnectAsync(timeout.Token))
            {
                return HealthCheckResult.Healthy("SQL Server reachable.");
            }

            LogServerUnreachable(logger);
            return HealthCheckResult.Unhealthy("SQL Server unreachable.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Our 3s probe budget elapsed — slow, not necessarily down; the
            // reason goes to the log, never into the response.
            LogProbeTimedOut(logger, ProbeTimeout.TotalSeconds);
            return HealthCheckResult.Unhealthy("SQL Server probe timed out.");
        }
        catch (OperationCanceledException)
        {
            // Host shutdown cancelled the probe — report, don't throw.
            return HealthCheckResult.Unhealthy("Readiness probe cancelled.");
        }
#pragma warning disable CA1031 // Deliberate: a health check must answer, never throw.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogServerUnreachableWithException(logger, ex);
            return HealthCheckResult.Unhealthy("SQL Server unreachable.");
        }
    }

    // Event ids 2100–2102: health probe family (1900–1903 = error envelope).
    [LoggerMessage(2100, LogLevel.Warning, "Readiness probe failed: SQL Server unreachable (CanConnect = false).")]
    private static partial void LogServerUnreachable(ILogger logger);

    [LoggerMessage(2101, LogLevel.Warning, "Readiness probe timed out after {ProbeTimeoutSeconds}s.")]
    private static partial void LogProbeTimedOut(ILogger logger, double probeTimeoutSeconds);

    [LoggerMessage(2102, LogLevel.Warning, "Readiness probe failed: SQL Server unreachable.")]
    private static partial void LogServerUnreachableWithException(ILogger logger, Exception exception);
}
