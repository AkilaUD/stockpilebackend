using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Stockpile.Domain.Entities.Media;
using Stockpile.Infrastructure.Persistence;

namespace Stockpile.Api.Controllers;

/// <summary>
/// Task 2G — immutable catalogue media (image-asset-plan.md §32). Bytes are
/// served from the platform storage root (<c>Storage:Root</c>, default
/// <c>.storage</c> — a repository-located, gitignored tree, H4) and are
/// content-addressed: the storage key carries the SHA-256, so the response is
/// immutable and cached forever. Proof of provenance for supplier media lives
/// in the slash-manifest sidecars beside the bytes; the <see cref="MediaAsset"/>
/// row (sha, size, content type) is the database's word for the file.
/// </summary>
[ApiController]
[ApiVersion(1.0)]
[Route("api/v{version:apiVersion}/catalog/media")]
[AllowAnonymous]
public sealed class MediaController(
    StockpileDbContext db,
    IConfiguration configuration,
    IWebHostEnvironment environment) : ControllerBase
{
    private const string KeyChars =
        "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789._-";

    /// <summary>
    /// Serve one immutable asset. 404 when the key is unknown to the database or
    /// its bytes are absent, and that 404 is explicitly uncacheable: the previous
    /// <c>[ResponseCache]</c> policy stamped <c>max-age=31536000</c> onto the
    /// miss too, so a transient gap — a storage root that was empty or replaced —
    /// stuck at every shared cache for a year and outlived its own repair.
    /// </summary>
    [HttpGet("{key}")]
    public async Task<IActionResult> Get(string key, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(key) || key.Length > 160 || key.Any(c => !KeyChars.Contains(c)))
            return MediaNotFound();

        var asset = await db.MediaAssets
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.StorageKey == key, cancellationToken);
        if (asset is null)
            return MediaNotFound();

        string fullPath = OriginalsPath(key);
        if (!System.IO.File.Exists(fullPath))
            return MediaNotFound();

        // Content-addressed storage: the etag IS the integrity hash, and the
        // asset is immutable by construction, so it may be cached forever.
        Response.Headers.CacheControl = "public, max-age=31536000, immutable";
        Response.Headers.ETag = $"\"{asset.Sha256}\"";

        var stream = new FileStream(
            fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        return File(stream, asset.ContentType, enableRangeProcessing: true);
    }

    private string OriginalsPath(string storageKey)
    {
        string? root = configuration["Storage:Root"];
        if (string.IsNullOrWhiteSpace(root))
            root = ".storage";

        // Same resolution as DataProtectionConfiguration: Storage:Root is
        // relative to the host content root (repo-located via appsettings).
        string storageRoot = Path.Combine(
            environment.ContentRootPath,
            root.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar));
        return Path.Combine(storageRoot, "originals", "catalogue", storageKey);
    }

    /// <summary>
    /// A 404 no shared cache may store. A miss is transient by construction: the
    /// storage root is re-checked against the harvest manifest on every boot
    /// (<c>CatalogueSeeder.EnsureMediaBytesAsync</c>), so the same key can answer
    /// 200 moments later.
    /// </summary>
    private NotFoundResult MediaNotFound()
    {
        Response.Headers.CacheControl = "no-store";
        return NotFound();
    }
}