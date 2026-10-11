using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Stockpile.Infrastructure.Persistence;

/// <summary>
/// Design-time factory for <c>dotnet ef</c> (migrations add/update) so the tool
/// never has to boot the Api host. Connection precedence: the
/// <c>ConnectionStrings__Stockpile</c> environment variable, then the
/// development LocalDB target. Production connections are never accepted here —
/// schema changes go through a reviewed migration applied by the deployment
/// step (architecture §9.2).
/// </summary>
public sealed class StockpileDesignTimeDbContextFactory : IDesignTimeDbContextFactory<StockpileDbContext>
{
    /// <summary>Development database; the only LocalDB database this project may touch (shared machine).</summary>
    public const string DevelopmentConnectionString =
        @"Server=(localdb)\MSSQLLocalDB;Database=Stockpile_Dev;Integrated Security=true;TrustServerCertificate=true";

    public StockpileDbContext CreateDbContext(string[] args)
    {
        string connectionString =
            Environment.GetEnvironmentVariable("ConnectionStrings__Stockpile")
            ?? DevelopmentConnectionString;

        var options = new DbContextOptionsBuilder<StockpileDbContext>()
            .UseSqlServer(connectionString)
            .Options;

        return new StockpileDbContext(options);
    }
}
