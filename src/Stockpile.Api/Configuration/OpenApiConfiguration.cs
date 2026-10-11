using Microsoft.Extensions.Configuration;

namespace Stockpile.Api.Configuration;

/// <summary>
/// Task 1.31 — the OpenAPI document gate (api.md §4, threat-model T4.6,
/// security-test-matrix H7). The versioned OpenAPI document is generated from
/// code only when <c>OpenApi:Enabled</c> is true. This can expose API
/// capabilities and should be enabled only when the deployment intends to
/// publish the API documentation.
/// </summary>
public static class OpenApiConfiguration
{
    /// <summary>Configuration key for the explicit opt-in.</summary>
    public const string EnabledKey = "OpenApi:Enabled";

    /// <summary>
    /// Whether the OpenAPI document is mapped for this host, as configured.
    /// </summary>
    public static bool IsEnabled(IConfiguration configuration, string environmentName) =>
        configuration.GetValue<bool>(EnabledKey);
}
