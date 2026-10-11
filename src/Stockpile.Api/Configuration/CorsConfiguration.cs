using Microsoft.Extensions.Configuration;

namespace Stockpile.Api.Configuration;

/// <summary>
/// Task 1.25 — the CORS origin allowlist (spec §13.6, threat-model §6).
/// Default deny: only origins present in configuration are ever admitted, and
/// the policy always pairs the origin check with <c>AllowCredentials()</c> —
/// so a wildcard origin would be wildcard-with-credentials, which is
/// forbidden and fails startup instead of being configured
/// (security-test-matrix C7/X12).
/// </summary>
public static class CorsConfiguration
{
    /// <summary>Named policy registered with <c>AddCors</c> and applied by <c>UseCors</c>.</summary>
    public const string PolicyName = "Stockpile";

    /// <summary>Configuration key holding the explicit origin allowlist.</summary>
    public const string AllowedOriginsKey = "Cors:AllowedOrigins";

    /// <summary>
    /// Response headers browsers may read cross-origin: correlation for error
    /// debugging plus the rate-limit headers (§13.7, task 1.26).
    /// </summary>
    public static readonly string[] ExposedHeaders =
    [
        "X-Correlation-Id",
        "X-RateLimit-Reset",
        "Retry-After",
    ];

    /// <summary>
    /// Reads and validates the allowlist from configuration. Empty (the
    /// default) = every origin is denied. Throws on <c>*</c>; configuration
    /// mistakes must fail startup, never degrade into a wildcard policy.
    /// </summary>
    public static HashSet<string> ReadAllowedOrigins(IConfiguration configuration) =>
        Create(configuration.GetSection(AllowedOriginsKey).Get<string[]>() ?? []);

    /// <summary>
    /// Builds the allowlist. Comparison is case-insensitive because RFC 6454
    /// treats scheme and host as case-insensitive; trailing slashes are
    /// trimmed because the serialized <c>Origin</c> header never carries one.
    /// </summary>
    public static HashSet<string> Create(IEnumerable<string> origins)
    {
        var allowlist = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var origin in origins)
        {
            if (string.IsNullOrWhiteSpace(origin))
            {
                // Tolerate empty entries (e.g. a trailing separator in an
                // environment-variable array); they match nothing.
                continue;
            }

            if (origin == "*")
            {
                throw new InvalidOperationException(
                    "Cors:AllowedOrigins must not contain '*': the Stockpile CORS policy always allows "
                    + "credentials, and wildcard origins with credentials are forbidden "
                    + "(spec §13.6, security-test-matrix C7/X12).");
            }

            allowlist.Add(origin.TrimEnd('/'));
        }

        return allowlist;
    }
}
