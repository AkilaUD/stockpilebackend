using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Stockpile.Api.Health;
using Stockpile.Infrastructure.Persistence;

namespace Stockpile.IntegrationTests.Health;

/// <summary>
/// Task 1.24 — a database that cannot be opened (missing database, wrong
/// server) must report Unhealthy instead of throwing: readiness probes need an
/// answer, and the failure detail stays server-side (T4.5).
/// </summary>
public sealed class DatabaseHealthCheckTests
{
    [Fact]
    public async Task Reports_unhealthy_when_the_database_cannot_be_opened()
    {
        // LocalDB does not auto-create databases on connect, so this probe
        // fails the way a downed or misconfigured server would — without
        // touching Stockpile_Dev / Stockpile_Test.
        var options = new DbContextOptionsBuilder<StockpileDbContext>()
            .UseSqlServer(
                "Server=(localdb)\\MSSQLLocalDB;Database=Stockpile_Probe_DoesNotExist;"
                + "Integrated Security=true;TrustServerCertificate=true;Connect Timeout=5")
            .Options;

        var check = new DatabaseHealthCheck(
            new StockpileDbContext(options), NullLogger<DatabaseHealthCheck>.Instance);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }
}
