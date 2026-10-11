namespace Stockpile.IntegrationTests.Testing;

/// <summary>
/// One shared <see cref="StockpileApiFactory"/> for every test that boots the
/// API — join this collection with <c>[Collection(ApiCollection.Name)]</c> and
/// constructor injection, never <c>IClassFixture&lt;StockpileApiFactory&gt;</c>.
/// </summary>
/// <remarks>
/// A second factory in the same process re-runs Program, whose Serilog
/// bootstrap creates a process-wide <c>ReloadableLogger</c>; the second host
/// then freezes an already-frozen logger ("The logger is already frozen") on
/// first resolve. Sharing one factory also means one migrate + seed of
/// Stockpile_Test instead of two racing the same database. Tests in this
/// collection therefore run serially — by design.
/// </remarks>
[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<StockpileApiFactory>
{
    /// <summary>xUnit collection name.</summary>
    public const string Name = "api";
}
