using Stockpile.Domain.Enums;

namespace Stockpile.Domain.Entities.Media;

/// <summary>
/// Uploaded/ingested media with mandatory provenance for non-supplier assets
/// (domain-model §4.7, image-asset-plan.md §32). Content-addressed by Sha256:
/// the hash gives dedupe and integrity verification. Guid v7 public id.
/// </summary>
public sealed class MediaAsset
{
    public Guid PublicId { get; set; } = Guid.CreateVersion7();

    /// <summary>Storage location key, e.g. <c>2026/06/brand/logo.png</c> — never a public URL.</summary>
    public string StorageKey { get; set; } = string.Empty;

    /// <summary>Sanitised original filename, kept for support/audit only.</summary>
    public string OriginalFileName { get; set; } = string.Empty;

    public string ContentType { get; set; } = string.Empty;

    public long ByteSize { get; set; }

    /// <summary>Lowercase hex SHA-256 of the bytes, nvarchar(64), unique.</summary>
    public string Sha256 { get; set; } = string.Empty;

    public int? Width { get; set; }

    public int? Height { get; set; }

    /// <summary>Focal point X ratio (0–1), decimal(5,4).</summary>
    public decimal? FocalX { get; set; }

    /// <summary>Focal point Y ratio (0–1), decimal(5,4).</summary>
    public decimal? FocalY { get; set; }

    /// <summary>Accessible description; required for meaningful imagery (WCAG 2.2 AA).</summary>
    public string? AltText { get; set; }

    public string? Caption { get; set; }

    // ---- Provenance: mandatory for any asset not uploaded by a supplier (§32) ----

    /// <summary>Where the asset came from, e.g. the live stockpile.lk page URL.</summary>
    public string? ProvenanceSourceUrl { get; set; }

    /// <summary>When it was retrieved from the source.</summary>
    public DateTime? ProvenanceRetrievedAt { get; set; }

    /// <summary>Who authorised the use internally.</summary>
    public string? ProvenanceAuthorisation { get; set; }

    /// <summary>Declared licence/terms of use.</summary>
    public string? ProvenanceLicense { get; set; }

    /// <summary>Public GUID of the uploading user; null = platform/system ingest
    /// (catalogue migration, automated imports). Referenced like every actor
    /// column: in-product uploads always carry the acting user's GUID.</summary>
    public Guid? UploadedBy { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public MediaAssetStatus Status { get; set; } = MediaAssetStatus.Active;
}
