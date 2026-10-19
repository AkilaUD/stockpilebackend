using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Stockpile.Domain.Entities.Content;
using Stockpile.Domain.Entities.Platform;
using Stockpile.Domain.Enums;
using Stockpile.Infrastructure.Persistence;

namespace Stockpile.Api.Persistence;

/// <summary>
/// Task 3E — content migration seeder (implementation-plan §3E). Reads the
/// auditable dataset (<c>tools/content-migrate/out/articles.json</c>, built by
/// <c>tests/content-dataset.mjs</c> from the reference news harvest) and loads
/// the editorial tree into Stockpile_Dev.
/// <para>
/// Same contract as <see cref="CatalogueSeeder"/>: a <see cref="SystemSetting"/>
/// marker records the dataset hash, so a matching marker skips the whole pass on
/// boot; a changed dataset with rows already present refuses to boot with a loud
/// operator message instead of silently mixing two harvests. Nothing is
/// invented — titles, bodies, dates, buckets and tags are the reference's own,
/// and the article body is the transcribed HTML with the theme's furniture
/// removed (see the dataset builder's header).
/// </para>
/// </summary>
internal static class ContentSeeder
{
    /// <summary>Marker key; the value is the SHA-256 of <c>articles.json</c>.</summary>
    private const string MarkerKey = "content.seeded.v1";

    private const int SlugMaxLength = 200;

    /// <summary>
    /// Serialises the seed pass across processes. <c>@LockOwner = 'Transaction'</c>
    /// ties the lock to the transaction that surrounds the pass, and the -1
    /// timeout waits rather than failing — a second host simply seeds after the
    /// first. See the gate in <see cref="SeedCoreAsync"/>.
    /// </summary>
    private const string AppLockSql =
        "EXEC sp_getapplock @Resource = 'stockpile:content-seed', @LockMode = 'Exclusive', "
        + "@LockOwner = 'Transaction', @LockTimeout = -1;";

    /// <summary>See <see cref="CatalogueSeeder"/> — camelCase files bound case-insensitively.</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static async Task SeedAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        using IServiceScope scope = services.CreateScope();
        StockpileDbContext db = scope.ServiceProvider.GetRequiredService<StockpileDbContext>();
        IConfiguration configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        IWebHostEnvironment environment = scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();

        string datasetPath = ResolveDatasetPath(configuration, environment.ContentRootPath);
        (ContentDataset dataset, string datasetHash) = await LoadDatasetAsync(datasetPath, cancellationToken);

        await SeedCoreAsync(db, dataset, datasetHash, cancellationToken);
    }

    private static async Task SeedCoreAsync(
        StockpileDbContext db,
        ContentDataset dataset,
        string datasetHash,
        CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // Cross-process single writer. `dotnet test` boots several API hosts at
        // once against one database (Stockpile_Test, AutoMigrate on), and the
        // gate below is a read-then-write: without this lock two hosts both see
        // "no rows, no marker" and both insert the same buckets, and the loser
        // dies on IX_ContentCategories_Slug. The app lock is transaction-owned,
        // so it is released by the commit or the rollback; the loser waits here
        // and then re-reads the gate.
        //
        // No CancellationToken at this one call site, deliberately — two guards
        // pull in opposite directions here:
        //  * architecture test I5 classifies the *whole* argument list of a raw
        //    SQL call and sanctions exactly the one-const-name shape, so adding
        //    `, ct` would itself fail the build (RawSqlInterpolationTests);
        //  * the statement is static reviewed text, and the lock waits under
        //    @LockTimeout = -1 inside a transaction that still observes `ct` at
        //    begin and commit, so this wait is the one thing here with nothing
        //    to cancel.
#pragma warning disable CA2016 // Reason above: the sanctioned single-argument shape leaves no token to forward.
        await db.Database.ExecuteSqlRawAsync(AppLockSql);
#pragma warning restore CA2016

        // ---- 0. Gate --------------------------------------------------------
        // Identical in shape to the catalogue's: matching marker → nothing to
        // do; stale marker over existing rows, or rows with no marker at all,
        // is an operator error that must not boot. Read *inside* the lock.
        SystemSetting? marker = await db.SystemSettings.FindAsync([MarkerKey], ct);
        bool hasContentRows = await db.ContentItems.AnyAsync(ct);
        if (marker is not null && marker.Value == datasetHash)
            return;
        if (marker is not null && hasContentRows)
        {
            throw new InvalidOperationException(
                $"Content seed marker '{MarkerKey}' is stale (dataset changed on disk) but content "
                + "rows already exist. Reset the Stockpile_Dev database (drop/recreate) and re-seed; "
                + "the seeder never merges over migrated rows.");
        }
        if (marker is null && hasContentRows)
        {
            throw new InvalidOperationException(
                $"Content rows exist without a seed marker '{MarkerKey}'. Reset the Stockpile_Dev "
                + "database and re-seed; partial migrations must not boot.");
        }

        // ---- 1. Buckets (the reference's news categories) -------------------
        var categoriesBySlug = new Dictionary<string, ContentCategory>(StringComparer.Ordinal);
        int sortOrder = 0;
        foreach (ContentCategoryRow row in dataset.Categories ?? [])
        {
            if (string.IsNullOrWhiteSpace(row.Slug))
                continue;

            sortOrder += 1;
            var category = new ContentCategory
            {
                Slug = Truncate(row.Slug, 160) ?? row.Slug,
                Name = Truncate(row.Name, 160) ?? row.Slug,
                SortOrder = sortOrder,
                IsActive = true,
            };
            categoriesBySlug[row.Slug] = category;
            db.ContentCategories.Add(category);
        }

        await db.SaveChangesAsync(ct);

        // ---- 2. Tags (editorial, shared across buckets) ---------------------
        var tagsBySlug = new Dictionary<string, ContentTag>(StringComparer.Ordinal);
        foreach (ArticleRow row in dataset.Articles ?? [])
        {
            foreach (string rawTag in row.Tags ?? [])
            {
                string slug = Slugify(rawTag, 160);
                if (slug.Length == 0 || tagsBySlug.ContainsKey(slug))
                    continue;

                var tag = new ContentTag { Slug = slug, Name = Truncate(rawTag.Trim(), 160) ?? slug };
                tagsBySlug[slug] = tag;
                db.ContentTags.Add(tag);
            }
        }

        await db.SaveChangesAsync(ct);

        // ---- 3. Articles, with their bucket and tag links -------------------
        int published = 0;
        foreach (ArticleRow row in dataset.Articles ?? [])
        {
            if (string.IsNullOrWhiteSpace(row.Slug) || string.IsNullOrWhiteSpace(row.Title))
                continue;

            var article = new ContentItem
            {
                Kind = ParseKind(row.Kind),
                Title = Truncate(row.Title, 300) ?? row.Slug,
                Slug = Truncate(row.Slug, SlugMaxLength) ?? row.Slug,
                Excerpt = Truncate(row.Excerpt, 1000),
                Body = row.Body ?? string.Empty,
                Status = ContentStatus.Published,
                PublishedAt = row.PublishedAt,
                Locale = "en",
            };
            db.ContentItems.Add(article);

            foreach (string categorySlug in row.Categories ?? [])
            {
                if (categoriesBySlug.TryGetValue(categorySlug, out ContentCategory? category))
                    db.ContentItemCategories.Add(new ContentItemCategory
                    {
                        ContentId = article.PublicId,
                        CategoryId = category.PublicId,
                    });
            }

            foreach (string rawTag in row.Tags ?? [])
            {
                string slug = Slugify(rawTag, 160);
                if (tagsBySlug.TryGetValue(slug, out ContentTag? tag))
                    db.ContentItemTags.Add(new ContentItemTag
                    {
                        ContentId = article.PublicId,
                        TagId = tag.PublicId,
                    });
            }

            published += 1;
            if (published % 25 == 0)
                await db.SaveChangesAsync(ct);
        }

        await db.SaveChangesAsync(ct);

        // ---- 4. Marker + commit --------------------------------------------
        if (marker is null)
        {
            db.SystemSettings.Add(new SystemSetting
            {
                Key = MarkerKey,
                Value = datasetHash,
                ValueType = SettingValueType.String,
            });
        }
        else
        {
            marker.Value = datasetHash;
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    // --------------------------------------------------------------- helpers

    private static ContentKind ParseKind(string? value)
        => Enum.TryParse(value, ignoreCase: true, out ContentKind kind) ? kind : ContentKind.NewsArticle;

    private static string ResolveDatasetPath(IConfiguration configuration, string contentRootPath)
    {
        string? overridePath = configuration["ContentSeed:DatasetPath"];
        if (!string.IsNullOrWhiteSpace(overridePath))
            return Path.GetFullPath(overridePath, contentRootPath);

        // In development this finds the dataset at the repository root; in a
        // published deployment the project includes it under the content root.
        string[] searchRoots = [contentRootPath, AppContext.BaseDirectory];
        foreach (string searchRoot in searchRoots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            for (DirectoryInfo? dir = new(searchRoot); dir is not null; dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, "tools", "content-migrate", "out", "articles.json");
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        throw new InvalidOperationException(
            "Content seed dataset not found. Expected the published file "
            + "'tools/content-migrate/out/articles.json' beside the API (or set "
            + "ContentSeed:DatasetPath to an absolute path). Run tests/content-dataset.mjs against "
            + "the news harvest to build it.");
    }

    private static async Task<(ContentDataset Dataset, string Hash)> LoadDatasetAsync(
        string datasetPath, CancellationToken ct)
    {
        string json = await File.ReadAllTextAsync(datasetPath, ct);
        var dataset = JsonSerializer.Deserialize<ContentDataset>(json, JsonOptions)
            ?? throw new InvalidOperationException($"Content dataset '{datasetPath}' is empty.");
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return (dataset, Convert.ToHexString(hash).ToLowerInvariant());
    }

    /// <summary>Tag slug: lower-case, letters/digits kept, runs of other characters collapsed to a dash.</summary>
    private static string Slugify(string value, int maxLength)
    {
        var builder = new StringBuilder(value.Length);
        bool lastWasDash = false;
        foreach (char character in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
                lastWasDash = false;
            }
            else if (builder.Length > 0 && !lastWasDash)
            {
                builder.Append('-');
                lastWasDash = true;
            }
        }

        string slug = builder.ToString().Trim('-');
        return slug.Length <= maxLength ? slug : slug[..maxLength].TrimEnd('-');
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        string trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength].TrimEnd();
    }

    // ------------------------------------------------------- dataset shapes

    private sealed record ContentDataset(
        List<ContentCategoryRow>? Categories,
        List<ArticleRow>? Articles);

    private sealed record ContentCategoryRow(string Slug, string Name, string? Kind);

    private sealed record ArticleRow(
        string Slug,
        string? Kind,
        string Title,
        string? Excerpt,
        DateTime? PublishedAt,
        List<string>? Categories,
        List<string>? Tags,
        string? Body,
        string? SourceUrl,
        List<string>? SourceImages);
}
