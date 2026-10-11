using Microsoft.Extensions.Configuration;
using Stockpile.Api.Configuration;

namespace Stockpile.IntegrationTests.Versioning;

/// <summary>
/// Task 1.31 — the OpenAPI gate (security-test-matrix H7, threat-model T4.6):
/// the versioned document is served only where configuration opts in, defaults
/// to disabled (fail-closed for every environment, including Production), and
/// Production REFUSES to boot on an explicit true — "OpenAPI exposed in
/// production" can never be a silently forgotten setting.
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
    public void Enabled_follows_the_configuration_opt_in(string environment, bool configured)
    {
        bool enabled = OpenApiConfiguration.IsEnabled(Config(OpenApiConfiguration.EnabledKey, configured.ToString()), environment);
        Assert.Equal(configured, enabled);
    }

    [Fact]
    public void Missing_key_is_disabled_in_every_environment()
    {
        Assert.False(OpenApiConfiguration.IsEnabled(Config(OpenApiConfiguration.EnabledKey, null), "Development"));
        Assert.False(OpenApiConfiguration.IsEnabled(Config(OpenApiConfiguration.EnabledKey, null), "Production"));
    }

    [Fact]
    public void Production_with_explicit_true_refuses_to_boot()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            OpenApiConfiguration.IsEnabled(Config(OpenApiConfiguration.EnabledKey, "true"), "Production"));

        Assert.Contains("must not be true in Production", exception.Message);
    }

    [Fact]
    public void Production_without_the_flag_stays_disabled()
    {
        Assert.False(OpenApiConfiguration.IsEnabled(Config(OpenApiConfiguration.EnabledKey, "false"), "Production"));
        Assert.False(OpenApiConfiguration.IsEnabled(Config(OpenApiConfiguration.EnabledKey, null), "Production"));
    }
}
