using Microsoft.Extensions.Configuration;
using Stockpile.Api.Configuration;

namespace Stockpile.IntegrationTests.Configuration;

/// <summary>
/// Task 1.33 — Data Protection key persistence (security-test-matrix H8,
/// threat-model §12/§13.14): keys are pinned to a file store outside the web
/// root and never the framework machine store %LOCALAPPDATA%\ASP.NET\
/// DataProtection-Keys; Production must name a durable, non-per-user store
/// explicitly; the purpose is scoped per environment.
/// </summary>
public sealed class DataProtectionConfigurationTests
{
    private static IConfiguration Config(string? path)
    {
        var settings = new Dictionary<string, string?>();
        if (path is not null)
        {
            settings[DataProtectionConfiguration.KeyStorePathKey] = path;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    private static string NewScratchDir() =>
        Path.Combine(Path.GetTempPath(), "stockpile-dp-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Development_defaults_to_a_content_root_store_that_is_created()
    {
        string contentRoot = NewScratchDir();

        DirectoryInfo store = DataProtectionConfiguration.ResolveKeyStore(
            Config(null), "Development", contentRoot);

        Assert.True(store.Exists, "the key-store directory must be created");
        Assert.EndsWith("dataprotection-keys", store.FullName, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(".storage", "dataprotection-keys"), store.FullName, StringComparison.Ordinal);
        Assert.False(IsWithinMachineStore(store.FullName), "dev store must stay outside the framework machine store");
    }

    [Fact]
    public void Machine_store_is_refused_in_non_production_environments() // H8
    {
        string machineStore = MachineStorePath();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            DataProtectionConfiguration.ResolveKeyStore(Config(machineStore), "Development", NewScratchDir()));

        Assert.Contains("machine store", exception.Message);
        Assert.Contains("H8", exception.Message);
    }

    [Fact]
    public void Machine_store_is_refused_even_written_as_an_environment_variable()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            DataProtectionConfiguration.ResolveKeyStore(
                Config("%LOCALAPPDATA%\\ASP.NET\\DataProtection-Keys"), "QA", NewScratchDir()));

        Assert.Contains("H8", exception.Message);
    }

    [Fact]
    public void Web_root_location_is_refused() // H8
    {
        string contentRoot = NewScratchDir();
        string underWebRoot = Path.Combine(contentRoot, "wwwroot", "keys");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            DataProtectionConfiguration.ResolveKeyStore(Config(underWebRoot), "Development", contentRoot));

        Assert.Contains("web root", exception.Message);
        Assert.Contains("H8", exception.Message);
    }

    [Fact]
    public void Production_requires_an_explicit_durable_key_store_path() // H8
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            DataProtectionConfiguration.ResolveKeyStore(Config(null), "Production", NewScratchDir()));

        Assert.Contains("Production", exception.Message);
        Assert.Contains("H8", exception.Message);
    }

    [Fact]
    public void Production_refuses_per_user_paths_even_outside_the_machine_store()
    {
        // %LOCALAPPDATA%\Temp is per-user but NOT the framework machine store:
        // the Production rule must catch it anyway (per-user locations are not
        // durable or shared across instances).
        string perUserPath = Path.Combine(Path.GetTempPath(), "my-app", "keys");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            DataProtectionConfiguration.ResolveKeyStore(Config(perUserPath), "Production", NewScratchDir()));

        Assert.Contains("per-user", exception.Message);
        Assert.Contains("H8", exception.Message);
    }

    [Fact]
    public void Production_explicit_shared_path_is_accepted_and_created()
    {
        // The test bin dir lives on the repo drive (outside %LOCALAPPDATA%), so
        // it is a valid stand-in for the shared volume a Production host uses.
        string root = Path.Combine(AppContext.BaseDirectory, "stockpile-dp-prod-" + Guid.NewGuid().ToString("N"));
        string contentRoot = Path.Combine(root, "contentroot");
        string sharedStore = Path.Combine(root, "shared", "dataprotection-keys");

        DirectoryInfo store;
        try
        {
            store = DataProtectionConfiguration.ResolveKeyStore(
                Config(sharedStore), "Production", contentRoot);

            Assert.True(store.Exists);
            Assert.False(IsWithinMachineStore(store.FullName));
            Assert.False(store.FullName.StartsWith(contentRoot, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void Application_purpose_is_scoped_per_environment() // threat-model §12
    {
        string dev = DataProtectionConfiguration.ApplicationName("Development");
        string prod = DataProtectionConfiguration.ApplicationName("Production");

        Assert.Equal("Stockpile.lk/Development", dev);
        Assert.Equal("Stockpile.lk/Production", prod);
        Assert.NotEqual(dev, prod);
    }

    private static string MachineStorePath()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "ASP.NET", "DataProtection-Keys");
    }

    private static bool IsWithinMachineStore(string fullPath)
    {
        string machine = MachineStorePath();
        return fullPath.StartsWith(machine, StringComparison.OrdinalIgnoreCase);
    }
}
