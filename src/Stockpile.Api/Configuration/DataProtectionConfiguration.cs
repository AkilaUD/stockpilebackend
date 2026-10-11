using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Stockpile.Api.Configuration;

/// <summary>
/// Task 1.33 — Data Protection key persistence (security-test-matrix H8,
/// threat-model §12/§13.14, architecture decision table). ASP.NET Core's
/// default machine-local key store (%LOCALAPPDATA%\ASP.NET\DataProtection-Keys)
/// is unusable for the multi-instance platform: keys would live on one host
/// only and session cookies would not survive a redeploy. The host persists
/// keys to a file location OUTSIDE the web root instead — the configured
/// <c>DataProtection:KeyStore:Path</c>, defaulting to
/// <c>.storage/dataprotection-keys</c> below the content root — scopes the
/// application purpose per environment (Development keys can never decrypt
/// Production cookies) and sets an explicit rotation lifetime. Startup REFUSES
/// any key store inside the framework machine store in every environment, and
/// in Production additionally refuses any per-user %LOCALAPPDATA% path, any
/// web-root path, and a missing explicit path (Production must name a durable
/// store rather than inherit the Development default).
/// </summary>
public static class DataProtectionConfiguration
{
    /// <summary>Configuration key for the explicit key-store directory.</summary>
    public const string KeyStorePathKey = "DataProtection:KeyStore:Path";

    /// <summary>Prefix for the Data Protection application purpose.</summary>
    public const string PurposePrefix = "Stockpile.lk";

    /// <summary>Key ring rotation window (threat-model §12).</summary>
    public const int KeyLifetimeDays = 90;

    /// <summary>
    /// Default key-store location: below the platform's storage root (the
    /// configured <c>Storage:Root</c>, else <c>.storage</c>) relative to the
    /// host content root — for the Api host that lands at the repository-root
    /// <c>.storage/dataprotection-keys</c>, the same tree media uses, so one
    /// ignored, documented location holds every generated artifact (H4).
    /// </summary>
    public static string DefaultKeyStorePath(IConfiguration configuration, string contentRootPath)
    {
        string? storageRoot = configuration["Storage:Root"];
        if (string.IsNullOrWhiteSpace(storageRoot))
        {
            storageRoot = ".storage";
        }

        return Path.Combine(
            contentRootPath,
            storageRoot.Replace('/', Path.DirectorySeparatorChar),
            "dataprotection-keys");
    }

    /// <summary>
    /// Resolves the Data Protection key-store directory, creating it on first
    /// use. Throws <see cref="InvalidOperationException"/> (the host will not
    /// boot) when the effective location is the framework machine store
    /// %LOCALAPPDATA%\ASP.NET\DataProtection-Keys — in every environment — when
    /// it sits inside the web root, or, in Production, when it is a per-user
    /// %LOCALAPPDATA% location or no explicit path was configured. Dev/QA may
    /// use the content-root default (which also lands outside the machine
    /// store); CI and local runs exercise the same code path.
    /// </summary>
    public static DirectoryInfo ResolveKeyStore(
        IConfiguration configuration, string environmentName, string contentRootPath)
    {
        bool isProduction =
            string.Equals(environmentName, Environments.Production, StringComparison.OrdinalIgnoreCase);

        string? configured = configuration[KeyStorePathKey];

        // Production must name its durable store explicitly. Checked before the
        // default path is built so the failure names the configuration gap, not
        // the (deliberately non-durable) Development fallback.
        if (isProduction && string.IsNullOrWhiteSpace(configured))
        {
            throw new InvalidOperationException(
                $"'{KeyStorePathKey}' must be configured explicitly in Production: the Development default "
                + $"({DefaultKeyStorePath(configuration, contentRootPath)}) "
                + $"is not a durable multi-instance location. Point it at the shared volume that holds the key ring "
                + $"(security-test-matrix H8, threat-model §12).");
        }

        string raw = string.IsNullOrWhiteSpace(configured)
            ? DefaultKeyStorePath(configuration, contentRootPath)
            : configured;

        // The framework machine store is what this task exists to replace; it
        // must never be accepted — not even in Development, or the guard would
        // be meaningless there too.
        raw = Environment.ExpandEnvironmentVariables(raw);
        string fullPath = Path.GetFullPath(raw);

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string machineStore = Path.Combine(localAppData, "ASP.NET", "DataProtection-Keys");
        if (IsWithin(fullPath, machineStore))
        {
            throw new InvalidOperationException(
                $"'{KeyStorePathKey}' resolves to the framework machine store ({fullPath}), which is forbidden — "
                + $"keys must live outside %LOCALAPPDATA%\\ASP.NET\\DataProtection-Keys so a second host or a redeploy "
                + $"can still read them (security-test-matrix H8, threat-model §12).");
        }

        // Per-user %LOCALAPPDATA% locations are fine for a Dev machine but are
        // not durable or shared, so Production must not use them either.
        if (isProduction && IsWithin(fullPath, localAppData))
        {
            throw new InvalidOperationException(
                $"'{KeyStorePathKey}' resolves to a per-user location under %LOCALAPPDATA% ({fullPath}), which is "
                + $"forbidden in Production — the key ring must live on durable shared storage reachable by every "
                + $"instance (security-test-matrix H8, threat-model §12).");
        }

        string webRoot = Path.Combine(contentRootPath, "wwwroot");
        if (IsWithin(fullPath, webRoot))
        {
            throw new InvalidOperationException(
                $"'{KeyStorePathKey}' resolves inside the web root ({fullPath}) — key material and the "
                + $"unencrypted purpose strings must never be reachable over HTTP (security-test-matrix H8, "
                + $"threat-model §12).");
        }

        Directory.CreateDirectory(fullPath);
        return new DirectoryInfo(fullPath);
    }

    /// <summary>
    /// Environment-scoped application purpose: "Stockpile.lk/&lt;environment&gt;".
    /// Data Protection keys are bound to the purpose, so a key ring generated
    /// in Development can never decrypt tokens/cookies issued by a Production
    /// host (threat-model §12).
    /// </summary>
    public static string ApplicationName(string environmentName) =>
        $"{PurposePrefix}/{environmentName}";

    private static bool IsWithin(string candidate, string parent)
    {
        string c = Path.TrimEndingDirectorySeparator(candidate);
        string p = Path.TrimEndingDirectorySeparator(parent);
        return c.Equals(p, StringComparison.OrdinalIgnoreCase)
            || c.StartsWith(p + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
