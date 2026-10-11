using Microsoft.Extensions.Configuration;
using Stockpile.Infrastructure.Configuration;

namespace Stockpile.IntegrationTests.Configuration;

/// <summary>
/// Task 1.32 — the Production configuration gate (security-test-matrix H1-H3,
/// threat-model §11). In Production the host must refuse to boot while a
/// required secret is missing (H2), still a placeholder (H1), or while
/// automatic migration is requested (H3); every other environment is
/// untouched. The check is whole-value on the manifest of required secret
/// keys, so a real connection string can never trip the placeholder rule.
/// </summary>
public sealed class ProductionConfigurationTests
{
    private const string RealConnectionString =
        "Server=sql.stockpile.lk,1433;Database=Stockpile_Prod;User Id=app;Password=ab12correct-horse;TrustServerCertificate=true";

    private static IConfiguration Config(string? connectionString, string? autoMigrate = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Stockpile"] = connectionString,
        };
        if (autoMigrate is not null)
        {
            settings[ProductionConfiguration.AutoMigrateKey] = autoMigrate;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    [Fact]
    public void The_contract_is_not_enforced_outside_production()
    {
        // Development/QA/Staging supply secrets through user-secrets and settings
        // files; the placeholder and migrate guards are Production rules only.
        ProductionConfiguration.Validate(Config("CHANGE_ME", autoMigrate: "true"), "Development");
        ProductionConfiguration.Validate(Config(null), "Qa");
        ProductionConfiguration.Validate(Config("changeme"), "Staging");
        ProductionConfiguration.Validate(Config("localhost"), "Testing");
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("\t", null)]
    public void Missing_required_secret_refuses_to_boot_in_production(string? value, string? autoMigrate) // H2
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            ProductionConfiguration.Validate(Config(value, autoMigrate), "Production"));

        Assert.Contains("ConnectionStrings:Stockpile", exception.Message);
        Assert.Contains("H2", exception.Message);
    }

    [Theory]
    [InlineData("CHANGE_ME")]
    [InlineData("change-me")]
    [InlineData("change_me")]
    [InlineData("REPLACE_ME")]
    [InlineData("replace me")]
    [InlineData("localhost")]
    [InlineData("xxx")]
    [InlineData("<your-secret-here>")]
    [InlineData("[replace-me]")]
    [InlineData("__PLACEHOLDER__")]
    public void Placeholder_secret_refuses_to_boot_in_production(string value) // H1
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            ProductionConfiguration.Validate(Config(value), "Production"));

        Assert.Contains("ConnectionStrings:Stockpile", exception.Message);
        Assert.Contains(value, exception.Message); // the message names the offending value
        Assert.Contains("H1", exception.Message);
    }

    [Fact]
    public void Placeholder_detection_is_per_value_case_insensitive()
    {
        Assert.True(ProductionConfiguration.IsPlaceholder("cHaNgEmE"));
        Assert.True(ProductionConfiguration.IsPlaceholder("LoCaLhOsT"));
        Assert.True(ProductionConfiguration.IsPlaceholder("  __placeholder__  "));

        // Precision: a real secret that merely *contains* a marker word is not
        // a placeholder — the check is whole-value by design, so legitimate
        // connection strings (which routinely contain Server= and Password=)
        // can never be refused by accident.
        Assert.False(ProductionConfiguration.IsPlaceholder(RealConnectionString));
        Assert.False(ProductionConfiguration.IsPlaceholder(
            "Server=localhost;Database=Stockpile_Dev;Integrated Security=true;TrustServerCertificate=true"));
    }

    [Fact]
    public void Realistic_connection_string_passes_production_validation()
    {
        ProductionConfiguration.Validate(Config(RealConnectionString, autoMigrate: "false"), "Production");
    }

    [Fact]
    public void AutoMigrate_true_refuses_to_boot_in_production() // H3
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            ProductionConfiguration.Validate(Config(RealConnectionString, autoMigrate: "true"), "Production"));

        Assert.Contains(ProductionConfiguration.AutoMigrateKey, exception.Message);
        Assert.Contains("H3", exception.Message);
    }

    [Fact]
    public void AutoMigrate_is_harmless_outside_production()
    {
        ProductionConfiguration.Validate(Config(RealConnectionString, autoMigrate: "true"), "Development");
        ProductionConfiguration.Validate(Config(RealConnectionString, autoMigrate: "true"), "Staging");
    }
}
