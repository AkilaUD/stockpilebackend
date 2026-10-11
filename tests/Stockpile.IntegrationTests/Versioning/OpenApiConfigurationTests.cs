using Microsoft.Extensions.Configuration;
using Stockpile.Api.Configuration;

namespace Stockpile.IntegrationTests.Versioning;

/// <summary>
/// Task 1.31 — the OpenAPI gate (security-test-matrix H7, threat-model T4.6):
/// the versioned document follows the explicit configuration value in every
/// environment.
/// </summary>
public sealed class OpenApiConfigurationTests
{
    private static IConfiguration Config(string key, string? value)
    {
        var settings = value is null
            ? new Dictionary<string, string?>()
            : new Dictionary<string, string?> { [key] = value };
        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    [Theory]
    [InlineData("Development", true)]
    [InlineData("Development", false)]
    [InlineData("Qa", true)]
    [InlineData("Staging", true)]
    [InlineData("Staging", false)]
    [InlineData("Production", true)]
    [InlineData("Production", false)]
    public void Enabled_follows_the_configuration_opt_in(string environment, bool configured)
    {
        bool enabled = OpenApiConfiguration.IsEnabled(Config(OpenApiConfiguration.EnabledKey, configured.ToString()), environment);
        Assert.Equal(configured, enabled);
    }

    [Fact]
    public void Missing_key_is_disabled()
    {
        Assert.False(OpenApiConfiguration.IsEnabled(Config(OpenApiConfiguration.EnabledKey, null), "Development"));
        Assert.False(OpenApiConfiguration.IsEnabled(Config(OpenApiConfiguration.EnabledKey, null), "Production"));
    }
}
