using Microsoft.Extensions.Logging;

namespace Stockpile.Api.Persistence;

/// <summary>
/// Structured logging for the catalogue media storage root (event ids 2400+).
/// The seeder reports a storage root that did not hold the bytes — the finding
/// that made every catalogue image answer 404 (2026-10-15) — so the operator
/// can point <c>Storage__Root</c> at a persistent folder instead of relying on
/// the repair at every boot.
/// </summary>
internal static partial class CatalogueStorageLog
{
    [LoggerMessage(
        EventId = 2400,
        Level = LogLevel.Warning,
        Message = "Catalogue media storage root was missing {Count} asset(s); restored them from the harvest "
            + "dataset into {Directory}. Point Storage__Root at a persistent folder outside the deployment "
            + "(deployment guide section 3) or this recurs after every deploy.")]
    public static partial void Restored(ILogger logger, int count, string directory);

    [LoggerMessage(
        EventId = 2401,
        Level = LogLevel.Error,
        Message = "Catalogue media storage root is missing {Count} asset(s) and the harvest dataset no longer "
            + "holds their bytes, so every affected catalogue image answers 404. Restore the packaged "
            + "tools/catalogue-migrate/out/media folder (deployment guide section 2).")]
    public static partial void Unavailable(ILogger logger, int count);
}
