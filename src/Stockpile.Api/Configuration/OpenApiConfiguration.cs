using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Stockpile.Api.Configuration;

/// <summary>
/// Task 1.31 — the OpenAPI document gate (api.md §4, threat-model T4.6,
/// security-test-matrix H7). The versioned OpenAPI document is generated from
/// code in Development/QA only; production is excluded from the build path by
/// a fail-closed default (<c>OpenApi:Enabled</c> is false in the base
/// appsettings.json; Development enables it, and the QA settings will mirror
/// that when deployed) and by STARTUP REFUSAL: an explicit true in Production
/// crashes the host instead of silently exposing the surface.
/// </summary>
public static class OpenApiConfiguration
{
    /// <summary>Configuration key for the explicit opt-in.</summary>
    public const string EnabledKey = "OpenApi:Enabled";

    /// <summary>
    /// Whether the OpenAPI document is mapped for this host. Disabled by
    /// default; Development (appsettings.Development.json) and QA settings opt
    /// in. Production can never enable it.
    /// </summary>
    public static bool IsEnabled(IConfiguration configuration, string environmentName)
    {
        bool enabled = configuration.GetValue<bool>(EnabledKey);

        if (enabled
            && string.Equals(environmentName, Environments.Production, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"'{EnabledKey}' must not be true in Production: the OpenAPI document is a "
                + "Development/QA surface (api.md §4, threat-model T4.6, security-test-matrix H7). "
                + "Remove the flag or set it to false.");
        }

        return enabled;
    }
}
