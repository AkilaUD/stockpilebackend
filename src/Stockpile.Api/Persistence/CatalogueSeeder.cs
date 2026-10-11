using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Stockpile.Domain.Entities.Catalogue;
using Stockpile.Domain.Entities.Media;
using Stockpile.Domain.Entities.Platform;
using Stockpile.Domain.Enums;
using Stockpile.Infrastructure.Persistence;

namespace Stockpile.Api.Persistence;

/// <summary>
/// Task 2E — catalogue migration seeder (implementation-plan §2E). Reads the
/// auditable harvest record (<c>tools/catalogue-migrate/out/catalogue.json</c>
/// + <c>media-manifest.json</c>) and loads it into Stockpile_Dev. Idempotent:
/// a <see cref="SystemSetting"/> marker records the dataset hash — a matching
/// marker skips the whole pass on boot; a changed dataset with rows already
/// present refuses to boot with a loud operator message instead of a silent
/// mix. Media bytes are copied once into the platform storage root
/// (<c>Storage:Root</c> → <c>originals/catalogue/</c>, gitignored) and every
/// append-only ledger (price history, stock adjustments) gets an honest
/// baseline row — nothing is invented to fill a gap.
/// </summary>
internal static class CatalogueSeeder
{
    /// <summary>Marker key; the value is the SHA-256 of <c>catalogue.json</c>.</summary>
    private const string MarkerKey = "catalogue.seeded.v3";

    /// <summary>
    /// JSON binding for the harvest records: the files are camelCase and record
    /// constructor parameters are PascalCase, and STJ's default constructor
    /// binding is case-sensitive — without this every record deserializes to
    /// nulls (silent data loss that only surfaces at insert time).
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static async Task SeedAsync(
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        using IServiceScope scope = services.CreateScope();
        StockpileDbContext db = scope.ServiceProvider.GetRequiredService<StockpileDbContext>();
        IConfiguration configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        IWebHostEnvironment environment = scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();

        string datasetPath = ResolveDatasetPath(configuration, environment.ContentRootPath);
        (CatalogueDataset dataset, string datasetHash) = await LoadDatasetAsync(datasetPath, cancellationToken);

        // Idempotency gate: matching marker → nothing to do. A stale marker with
        // rows already present is an operator error: reset the Dev database and
        // re-seed, never silently overwrite half of a live-looking catalogue.
        SystemSetting? marker = await db.SystemSettings.FindAsync([MarkerKey], cancellationToken);
        bool hasCatalogueRows = await db.Categories.AnyAsync(cancellationToken);
        if (marker is not null && marker.Value == datasetHash)
            return;
        if (marker is not null && hasCatalogueRows)
        {
            throw new InvalidOperationException(
                $"Catalogue seed marker '{MarkerKey}' is stale (dataset changed on disk) but catalogue "
                + "rows already exist. Reset the Stockpile_Dev database (drop/recreate) and re-seed; "
                + "the seeder never merges over migrated rows.");
        }
        if (marker is null && hasCatalogueRows)
        {
            throw new InvalidOperationException(
                $"Catalogue rows exist without a seed marker '{MarkerKey}'. Reset the Stockpile_Dev "
                + "database and re-seed; partial migrations must not boot.");
        }

        await SeedCoreAsync(db, configuration, environment, dataset, datasetHash, cancellationToken);
    }

    private static async Task SeedCoreAsync(
        StockpileDbContext db,
        IConfiguration configuration,
        IWebHostEnvironment environment,
        CatalogueDataset dataset,
        string datasetHash,
        CancellationToken ct)
    {
        string outDir = Path.GetDirectoryName(ResolveDatasetPath(configuration, environment.ContentRootPath))
            ?? throw new InvalidOperationException("Cannot locate the harvest output directory.");
        string manifestPath = Path.Combine(outDir, "media-manifest.json");
        if (!File.Exists(manifestPath))
            throw new InvalidOperationException($"Harvest media manifest missing at '{manifestPath}' — run harvest.mjs first.");
        List<MediaManifestEntry> manifest = JsonSerializer.Deserialize<List<MediaManifestEntry>>(
            await File.ReadAllTextAsync(manifestPath, ct), JsonOptions) ?? [];

        DateTime seededAt = DateTime.UtcNow;

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // ---- 1. Media bytes + MediaAsset rows (content-addressed, deduped) --
        string originalsRoot = OriginalsRoot(configuration, environment);
        string catalogueDir = Path.Combine(originalsRoot, "catalogue");
        Directory.CreateDirectory(catalogueDir);

        var mediaByKey = new Dictionary<string, MediaAsset>(StringComparer.Ordinal);
        foreach (MediaManifestEntry entry in manifest)
        {
            string storageKey = Path.GetFileName(entry.FileRelative);
            string source = Path.Combine(outDir, entry.FileRelative);
            string destination = Path.Combine(catalogueDir, storageKey);
            if (!File.Exists(source))
            {
                throw new InvalidOperationException(
                    $"Harvest media '{source}' is missing. Re-run harvest.mjs and try again — a "
                    + "catalogue must never boot with broken image references.");
            }

            if (File.Exists(destination))
            {
                // Idempotent: keep the existing copy when the size matches.
                if (new FileInfo(destination).Length != new FileInfo(source).Length)
                    File.Copy(source, destination, overwrite: true);
            }
            else
            {
                File.Copy(source, destination);
            }

            if (!mediaByKey.ContainsKey(storageKey))
            {
                mediaByKey[storageKey] = new MediaAsset
                {
                    StorageKey = storageKey,
                    OriginalFileName = Path.GetFileName(new Uri(entry.SourceUrl).AbsolutePath),
                    ContentType = entry.ContentType,
                    ByteSize = entry.ByteSize,
                    Sha256 = entry.Sha256,
                    ProvenanceSourceUrl = entry.SourceUrl,
                    ProvenanceRetrievedAt = entry.FetchedAt,
                    ProvenanceAuthorisation = entry.ProvenanceAuthorisation,
                    ProvenanceLicense = entry.Licence,
                    CreatedAt = seededAt,
                };
            }
        }
        db.MediaAssets.AddRange(mediaByKey.Values);

        // ---- 2. Reference rows: geography already seeded; brands/manufacturers --
        var brandsByCode = new Dictionary<string, Brand>(StringComparer.OrdinalIgnoreCase);
        var manufacturersByCode = new Dictionary<string, Manufacturer>(StringComparer.OrdinalIgnoreCase);
        foreach (string brandName in DistinctNames(dataset.Products, p => p.Brand))
        {
            string code = Slugify(brandName, 80);
            if (!brandsByCode.ContainsKey(code))
                brandsByCode[code] = new Brand { Code = code, Name = brandName };
        }
        foreach (string manufacturerName in DistinctNames(dataset.Products, p => p.Manufacturer))
        {
            string code = Slugify(manufacturerName, 80);
            if (!manufacturersByCode.ContainsKey(code))
                manufacturersByCode[code] = new Manufacturer { Code = code, Name = manufacturerName };
        }
        db.Brands.AddRange(brandsByCode.Values);
        db.Manufacturers.AddRange(manufacturersByCode.Values);

        // ---- 3. Units of measure (from the harvested retail unit labels) ----
        var unitsByLabel = new Dictionary<string, UnitOfMeasure>(StringComparer.Ordinal);
        foreach (ProductRow product in dataset.Products)
        {
            string? label = ProductSpecValue(product, "unit of measure") ?? product.Unit;
            if (string.IsNullOrWhiteSpace(label) || unitsByLabel.ContainsKey(label))
                continue;
            unitsByLabel[label] = UnitFromLabel(label);
        }
        db.UnitOfMeasures.AddRange(unitsByLabel.Values);

        // ---- 4. Attribute definitions (spec labels, shared across categories) --
        var attributesByLabel = new Dictionary<string, AttributeDefinition>(StringComparer.Ordinal);
        foreach (ProductRow product in dataset.Products)
        {
            foreach (SpecRow spec in product.Specs)
            {
                if (string.IsNullOrWhiteSpace(spec.Label) || attributesByLabel.ContainsKey(spec.Label))
                    continue;
                attributesByLabel[spec.Label] = new AttributeDefinition
                {
                    Code = Slugify(spec.Label, 80),
                    DisplayName = spec.Label,
                    DataType = AttributeDataType.Text,
                    Searchable = true,
                };
            }
        }
        db.AttributeDefinitions.AddRange(attributesByLabel.Values);
        await db.SaveChangesAsync(ct);

        // ---- 5. Categories (materialized path; Slug = full path) ------------
        var categoriesByPath = new Dictionary<string, Category>(StringComparer.Ordinal);
        foreach (CategoryRow category in dataset.Categories.OrderBy(c => c.Path.Length).ThenBy(c => c.Path, StringComparer.Ordinal))
        {
            string? parentPath = ParentOf(category.Path);
            var entity = new Category
            {
                Slug = category.Path,
                Name = Truncate(category.Name, 160),
                NameSi = string.IsNullOrWhiteSpace(category.NameSi) ? null : Truncate(category.NameSi, 160),
                Description = Truncate(category.Description, 2000),
                MaterializedPath = category.Path,
                ParentId = parentPath is not null && categoriesByPath.TryGetValue(parentPath, out var parent)
                    ? parent.PublicId
                    : null,
                IconKey = category.Path.Split('/')[0],
                SortOrder = parentPath is not null && categoriesByPath.TryGetValue(parentPath, out var p)
                    ? categoriesByPath.Values.Count(v => v.ParentId == p.PublicId)
                    : categoriesByPath.Values.Count(v => v.ParentId is null),
                IsActive = true,
            };
            categoriesByPath[category.Path] = entity;
        }
        db.Categories.AddRange(categoriesByPath.Values);
        await db.SaveChangesAsync(ct);

        // ---- 6. Suppliers + public profiles --------------------------------
        var supplierBySlug = new Dictionary<string, Supplier>(StringComparer.Ordinal);
        foreach (SupplierRow supplier in dataset.Suppliers)
        {
            Guid? logoMediaId = supplier.LogoFile is { } logo
                && mediaByKey.TryGetValue(Path.GetFileName(logo), out var logoAsset)
                    ? logoAsset.PublicId
                    : null;

            var entity = new Supplier
            {
                Slug = Truncate(supplier.Slug, 160),
                DisplayName = Truncate(supplier.DisplayName, 200),
                Status = SupplierStatus.Active,
                VerificationState = supplier.Verified ? VerificationState.Verified : VerificationState.Unverified,
                Story = supplier.About is { Length: > 0 } ? Truncate(supplier.About, 6000) : null,
                LogoMediaId = logoMediaId,
                CreatedAt = seededAt,
                Profile = new SupplierProfile
                {
                    Phone = Truncate(supplier.Phone, 30),
                    Email = Truncate(supplier.Email, 160),
                    Website = Truncate(supplier.Website, 300),
                    OperationAddress = Truncate(supplier.Location, 300),
                    Latitude = supplier.LatLng?.Lat,
                    Longitude = supplier.LatLng?.Lng,
                },
            };
            supplierBySlug[supplier.Slug] = entity;
        }
        foreach (Supplier supplier in supplierBySlug.Values)
            db.Suppliers.Add(supplier);
        await db.SaveChangesAsync(ct);

        // ---- 7. Products + price/stock/ledger/media children (chunked) -------
        // Children carry explicit ProductId (the domain has no navigation
        // properties; the store reads them through explicit joins).
        var rowAttributeIds = attributesByLabel.ToDictionary(kv => kv.Key, kv => kv.Value.PublicId);
        foreach (ProductRow[] chunk in dataset.Products.OrderBy(p => p.Slug, StringComparer.Ordinal).Chunk(250))
        {
            foreach (ProductRow product in chunk)
            {
                if (!supplierBySlug.TryGetValue(product.SupplierSlug, out var supplier))
                {
                    throw new InvalidOperationException(
                        $"Product '{product.Slug}' references unknown supplier '{product.SupplierSlug}'.");
                }

                string? categoryPath = product.CategoryPath.LastOrDefault()?.Slug;
                if (categoryPath is null || !categoriesByPath.TryGetValue(categoryPath, out var category))
                {
                    throw new InvalidOperationException(
                        $"Product '{product.Slug}' references unknown category '{categoryPath ?? "(none)"}'.");
                }

                decimal price = decimal.Parse(product.Price, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
                int? leadTime = ParseLeadingInt(ProductSpecValue(product, "lead time days"));
                Guid productId = Guid.CreateVersion7();

                var entity = new Product
                {
                    PublicId = productId,
                    SupplierId = supplier.PublicId,
                    CategoryId = category.PublicId,
                    ItemType = ItemType.PhysicalProduct,
                    Name = Truncate(product.Name, 300),
                    Slug = Truncate(product.Slug, 300),
                    Sku = Truncate(product.Sku, 100),
                    Status = ProductStatus.Published,
                    BrandId = product.Brand is { Length: > 0 }
                        ? brandsByCode.GetValueOrDefault(Slugify(product.Brand, 80))?.PublicId
                        : null,
                    ManufacturerId = product.Manufacturer is { Length: > 0 }
                        ? manufacturersByCode.GetValueOrDefault(Slugify(product.Manufacturer, 80))?.PublicId
                        : null,
                    Description = Truncate(product.Description, 8000),
                    CountryOfOriginCode = CountryCode(ProductSpecValue(product, "country of origin")),
                    LeadTimeDays = leadTime,
                    PublishedAt = seededAt,
                    CreatedAt = seededAt,
                };

                var children = new List<object>
                {
                    new ProductPrice
                    {
                        ProductId = productId,
                        CurrencyCode = "LKR",
                        Amount = price,
                        PriceState = PriceState.Public,
                        EffectiveAt = seededAt,
                        IsPublic = true,
                    },
                    new ProductPriceHistory
                    {
                        ProductId = productId,
                        CurrencyCode = "LKR",
                        PreviousAmount = price,
                        NewAmount = price,
                        ChangedAt = seededAt,
                        Reason = "Initial import from stockpile.lk (catalogue migration).",
                    },
                    new ProductStock
                    {
                        ProductId = productId,
                        IsStockTracked = false,
                        StockState = StockStateFrom(product.Availability),
                        OnHand = 0,
                    },
                    new StockAdjustment
                    {
                        ProductId = productId,
                        Delta = 0,
                        Reason = "Initial stock baseline from stockpile.lk (catalogue migration); not live-tracked.",
                        OccurredAt = seededAt,
                    },
                };

                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (SpecRow spec in product.Specs)
                {
                    if (string.IsNullOrWhiteSpace(spec.Label) || string.IsNullOrWhiteSpace(spec.Value))
                        continue;
                    if (!seen.Add(spec.Label) || !rowAttributeIds.TryGetValue(spec.Label, out Guid attributeId))
                        continue;
                    children.Add(new ProductAttributeValue
                    {
                        ProductId = productId,
                        AttributeId = attributeId,
                        ValueText = Truncate(spec.Value, 600),
                    });
                }

                var images = (product.Images ?? []).ToList();
                for (int index = 0; index < images.Count; index++)
                {
                    string? file = images[index].File;
                    if (file is null)
                        continue;
                    string key = Path.GetFileName(file);
                    if (!mediaByKey.TryGetValue(key, out var mediaAsset))
                    {
                        throw new InvalidOperationException(
                            $"Product '{product.Slug}' references media '{key}' missing from the manifest.");
                    }

                    children.Add(new ProductMedia
                    {
                        ProductId = productId,
                        MediaAssetId = mediaAsset.PublicId,
                        SortOrder = index,
                        IsPrimary = images[index].IsPrimary,
                        AltText = Truncate(product.Name, 300),
                    });
                }

                db.Products.Add(entity);
                db.AddRange(children);
            }

            await db.SaveChangesAsync(ct);
        }

        // ---- 8. Marker + commit ---------------------------------------------
        SystemSetting? marker = await db.SystemSettings.FindAsync([MarkerKey], ct);
        if (marker is null)
            db.SystemSettings.Add(new SystemSetting { Key = MarkerKey, Value = datasetHash, ValueType = SettingValueType.String });
        else
            marker.Value = datasetHash;

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    // ---------------------------------------------------------------- helpers

    private static string ResolveDatasetPath(IConfiguration configuration, string contentRootPath)
    {
        string? overridePath = configuration["CatalogueSeed:DatasetPath"];
        if (!string.IsNullOrWhiteSpace(overridePath))
            return Path.GetFullPath(overridePath);

        // Walk up from the host content root to the repository root; the dataset
        // lives at tools/catalogue-migrate/out/catalogue.json.
        for (DirectoryInfo? dir = new(contentRootPath); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "tools", "catalogue-migrate", "out", "catalogue.json");
            if (File.Exists(candidate))
                return candidate;
        }

        throw new InvalidOperationException(
            "Catalogue seed dataset not found. Expected tools/catalogue-migrate/out/catalogue.json "
            + "below the repository root (or set CatalogueSeed:DatasetPath). Run harvest.mjs first.");
    }

    private static async Task<(CatalogueDataset Dataset, string Hash)> LoadDatasetAsync(
        string datasetPath, CancellationToken ct)
    {
        string json = await File.ReadAllTextAsync(datasetPath, ct);
        var dataset = JsonSerializer.Deserialize<CatalogueDataset>(json, JsonOptions)
            ?? throw new InvalidOperationException($"Catalogue dataset '{datasetPath}' is empty.");
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return (dataset, Convert.ToHexString(hash).ToLowerInvariant());
    }

    private static string OriginalsRoot(IConfiguration configuration, IWebHostEnvironment environment)
    {
        string? root = configuration["Storage:Root"];
        if (string.IsNullOrWhiteSpace(root))
            root = ".storage";
        return Path.Combine(
            environment.ContentRootPath,
            root.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar),
            "originals");
    }

    private static string? ParentOf(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash > 0 ? path[..slash] : null;
    }

    private static string Slugify(string value, int maxLength)
    {
        var builder = new StringBuilder(value.Length);
        foreach (char c in value.Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c))
                builder.Append(c);
            else if (c is ' ' or '-' or '_' or '.' or '/' or '&' or '+')
                builder.Append('-');
        }

        string slug = builder.ToString();
        while (slug.Contains("--", StringComparison.Ordinal))
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        slug = slug.Trim('-');
        if (slug.Length == 0)
            slug = "item";
        return slug.Length <= maxLength ? slug : slug[..maxLength].TrimEnd('-');
    }

    private static string Truncate(string? value, int maxLength)
        => string.IsNullOrEmpty(value) ? string.Empty
            : value.Length <= maxLength ? value : value[..maxLength];

    private static IEnumerable<string> DistinctNames(IEnumerable<ProductRow> products, Func<ProductRow, string?> selector)
        => products
            .Select(selector)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal);

    private static string? ProductSpecValue(ProductRow product, string labelKey)
        => product.Specs
            .FirstOrDefault(spec => spec.Label.Equals(labelKey, StringComparison.OrdinalIgnoreCase))
            ?.Value
            ?.Trim();

    private static int? ParseLeadingInt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        string trimmed = value.Trim();
        if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            return parsed;
        return null;
    }

    /// <summary>Known origin names → ISO 3166-1 alpha-2. Unknown/ambiguous → null (honest).</summary>
    private static readonly Dictionary<string, string> CountryCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["China"] = "CN",
        ["India"] = "IN",
        ["Sri Lanka"] = "LK",
        ["Japan"] = "JP",
        ["Brazil"] = "BR",
        ["Malaysia"] = "MY",
        ["Germany"] = "DE",
        ["Taiwan"] = "TW",
        ["U.S.A."] = "US",
        ["USA"] = "US",
        ["Italy"] = "IT",
        ["Saudi Arabia"] = "SA",
        ["South Korea"] = "KR",
        ["Pakistan"] = "PK",
        ["Canada"] = "CA",
        ["Thailand"] = "TH",
        ["Greece"] = "GR",
        ["South Africa"] = "ZA",
    };

    private static string? CountryCode(string? originName)
        => originName is not null
            && CountryCodes.TryGetValue(originName, out string? code)
            && code is not null
                ? code
                : null;

    private static StockState StockStateFrom(string? availability)
        => availability switch
        {
            "InStock" => StockState.InStock,
            "OutOfStock" => StockState.OutOfStock,
            "PreOrder" => StockState.MadeToOrder,
            _ => StockState.EnquireForAvailability,
        };

    private static UnitOfMeasure UnitFromLabel(string label)
        => label.Trim() switch
        {
            "Nos." => new UnitOfMeasure { Code = "each", Name = "Each", PluralName = "Each", DimensionKind = DimensionKind.Count },
            "Linear feet (ft)" => new UnitOfMeasure { Code = "lft", Name = "Linear Foot", PluralName = "Linear Feet", DimensionKind = DimensionKind.Length },
            "Sq. Feet (ft2)" => new UnitOfMeasure { Code = "sqft", Name = "Square Foot", PluralName = "Square Feet", DimensionKind = DimensionKind.Area },
            "Kilograms (kg)" => new UnitOfMeasure { Code = "kg", Name = "Kilogram", PluralName = "Kilograms", DimensionKind = DimensionKind.Mass },
            "Meters (m)" => new UnitOfMeasure { Code = "m", Name = "Metre", PluralName = "Metres", DimensionKind = DimensionKind.Length },
            "Sq. Meters (m2)" => new UnitOfMeasure { Code = "sqm", Name = "Square Metre", PluralName = "Square Metres", DimensionKind = DimensionKind.Area },
            "Millimeters (mm)" => new UnitOfMeasure { Code = "mm", Name = "Millimetre", PluralName = "Millimetres", DimensionKind = DimensionKind.Length },
            _ => new UnitOfMeasure { Code = Slugify(label, 80), Name = label.Trim(), PluralName = label.Trim(), DimensionKind = DimensionKind.Count },
        };

    // ------------------------------------------------------- dataset DTOs

    internal sealed record CatalogueDataset(
        List<CategoryRow> Categories,
        List<ProductRow> Products,
        List<SupplierRow> Suppliers);

    internal sealed record CategoryRow(
        string Slug, string Path, string Name, string? NameSi, string? Description, int? ProductCount, string? ImageFile);

    internal sealed record ProductRow(
        string Slug, string Url, string Sku, string Name, string? Brand, string? Manufacturer,
        List<CategoryRefRow> CategoryPath, string SupplierSlug, string SupplierName,
        string Price, string Currency, string Availability, string PriceState,
        string? Description, List<SpecRow> Specs, int? LeadTimeDays, string? Unit,
        string? Material, string? Warranty, List<ImageRow> Images);

    internal sealed record CategoryRefRow(string Name, string Slug);

    internal sealed record SpecRow(string Label, string Value);

    internal sealed record ImageRow(string Url, string? File, bool IsPrimary);

    internal sealed record SupplierRow(
        string Slug, string DisplayName, string Url, bool Verified, string? About,
        string? Website, string? Phone, string? Email, string? Location,
        LatLngRow? LatLng, int? ProductsCount, string? LogoFile);

    internal sealed record LatLngRow(double Lat, double Lng);

    internal sealed record MediaManifestEntry(
        string SourceUrl, string FileRelative, string Sha256, string ContentType,
        long ByteSize, DateTime FetchedAt, string Licence, string ProvenanceAuthorisation);
}