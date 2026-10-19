using System.Net;
using Stockpile.Api.Persistence;
using Stockpile.IntegrationTests.Testing;

namespace Stockpile.IntegrationTests.Catalogue;

/// <summary>
/// Regression for the 2026-10-15 "images not loading" report. The catalogue
/// seeder copies media bytes only while it seeds, and that pass is marker-gated,
/// so a storage root that was replaced or wiped after the first seed was never
/// repopulated: the marker still matched, no seed ran, and every
/// <c>GET /api/v1/catalog/media/{key}</c> answered 404 for the life of the
/// deployment. <see cref="CatalogueSeeder.EnsureMediaBytesAsync"/> re-checks the
/// root against the database on every boot;
/// <see cref="CatalogueSeeder.RestoreMissingBytes"/> is its filesystem repair.
/// </summary>
public sealed class CatalogueMediaRestoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "stockpile-media-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Restores_the_missing_bytes_and_leaves_the_present_ones_alone()
    {
        string mediaSourceDir = Path.Combine(_root, "out", "media");
        string catalogueDir = Path.Combine(_root, "storage", "originals", "catalogue");
        Directory.CreateDirectory(mediaSourceDir);
        Directory.CreateDirectory(catalogueDir);

        File.WriteAllText(Path.Combine(mediaSourceDir, "m-aaa.png"), "AAA");
        File.WriteAllText(Path.Combine(mediaSourceDir, "m-bbb.png"), "BBB");
        File.WriteAllText(Path.Combine(catalogueDir, "m-bbb.png"), "BBB");

        // The database's rows decide what must exist: a wiped root is simply the
        // case where none of these files are present.
        string[] storageKeys = ["m-aaa.png", "m-bbb.png"];

        CatalogueSeeder.MediaRestoreReport report =
            CatalogueSeeder.RestoreMissingBytes(mediaSourceDir, catalogueDir, storageKeys);

        Assert.Equal(1, report.Copied);
        Assert.Equal(0, report.MissingSources);
        Assert.Equal("AAA", File.ReadAllText(Path.Combine(catalogueDir, "m-aaa.png")));
        Assert.Equal("BBB", File.ReadAllText(Path.Combine(catalogueDir, "m-bbb.png")));

        // Idempotent: every later boot has nothing left to restore.
        Assert.Equal(
            0,
            CatalogueSeeder.RestoreMissingBytes(mediaSourceDir, catalogueDir, storageKeys).Copied);
    }

    [Fact]
    public void Reports_bytes_the_dataset_no_longer_holds_without_failing_startup()
    {
        string mediaSourceDir = Path.Combine(_root, "out", "media");
        string catalogueDir = Path.Combine(_root, "storage", "originals", "catalogue");
        Directory.CreateDirectory(catalogueDir);

        // The documented post-seed cleanup prunes the dataset's media folder once
        // the storage root is populated. A root that is replaced after that
        // cannot heal, and that is reported — never thrown, or the API would not
        // boot at all.
        CatalogueSeeder.MediaRestoreReport report =
            CatalogueSeeder.RestoreMissingBytes(mediaSourceDir, catalogueDir, ["m-gone.png"]);

        Assert.Equal(0, report.Copied);
        Assert.Equal(1, report.MissingSources);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a leaked temp directory must not fail the run.
        }
    }
}

/// <summary>
/// A media 404 must never be stored by a shared cache. The removed
/// <c>[ResponseCache]</c> policy stamped <c>max-age=31536000</c> onto the miss as
/// well as the hit, so a transient gap — an empty or replaced storage root —
/// stuck at the Vercel edge for a year and outlived its own repair (2026-10-15).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class CatalogueMediaCacheHeaderTests(StockpileApiFactory factory)
{
    [Fact]
    public async Task Unknown_media_key_answers_404_that_no_cache_may_store()
    {
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync(
            $"/api/v1/catalog/media/m-{new string('0', 64)}.png");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.True(
            response.Headers.CacheControl?.NoStore ?? false,
            $"A media 404 must be uncacheable; response carried '{response.Headers.CacheControl}'.");
    }
}
