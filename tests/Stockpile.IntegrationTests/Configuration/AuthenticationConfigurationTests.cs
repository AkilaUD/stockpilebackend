using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Stockpile.Api.Configuration;

namespace Stockpile.IntegrationTests.Configuration;

/// <summary>
/// Task 1.34 — cookie authentication (security-test-matrix A14, architecture
/// §8.1): the flags are fixed at secure values (HttpOnly, SameSite=Lax, Secure
/// when required), the ticket slides on the configured window, Production
/// refuses to serve cookies without the Secure attribute, and absurd window
/// values refuse boot.
/// </summary>
public sealed class AuthenticationConfigurationTests
{
    private static CookieAuthenticationOptions Resolve(
        string environmentName, params (string Key, string Value)[] settings)
    {
        var values = new Dictionary<string, string?>();
        foreach ((string key, string value) in settings)
        {
            values[key] = value;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        AuthenticationConfiguration.AddStockpileCookieAuthentication(
            services, configuration, environmentName);

        using var provider = services.BuildServiceProvider();
        // AddCookie registers its configurator for the NAMED scheme; the auth
        // handler resolves through IOptionsMonitor.Get(scheme), and so do we —
        // IOptions<T>.Value would return the unused unnamed default.
        return provider
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);
    }

    [Fact]
    public void Defaults_are_the_architecture_8_1_secure_flags()
    {
        var options = Resolve("Development");

        Assert.Equal(AuthenticationConfiguration.CookieName, options.Cookie.Name);
        Assert.True(options.Cookie.HttpOnly, "HttpOnly must be fixed at true (A14)");
        Assert.Equal(SameSiteMode.Lax, options.Cookie.SameSite);
        Assert.Equal(CookieSecurePolicy.SameAsRequest, options.Cookie.SecurePolicy);
        Assert.True(options.SlidingExpiration, "the ticket must slide (architecture 8.1)");
        Assert.Equal(TimeSpan.FromMinutes(AuthenticationConfiguration.DefaultSessionMinutes), options.ExpireTimeSpan);
    }

    [Fact]
    public void RequireHttps_switches_the_secure_policy_to_always()
    {
        var options = Resolve("Development", (AuthenticationConfiguration.RequireHttpsKey, "true"));
        Assert.Equal(CookieSecurePolicy.Always, options.Cookie.SecurePolicy);
    }

    [Fact]
    public void Session_window_is_configurable_and_stays_sliding()
    {
        var options = Resolve("Development", (AuthenticationConfiguration.SessionMinutesKey, "45"));
        Assert.Equal(TimeSpan.FromMinutes(45), options.ExpireTimeSpan);
        Assert.True(options.SlidingExpiration);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("721")]
    public void Out_of_range_session_windows_refuse_boot(string minutes)
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            Resolve("Development", (AuthenticationConfiguration.SessionMinutesKey, minutes)));

        Assert.Contains("A14", exception.Message);
        Assert.Contains("12", exception.Message); // the message names the 12-hour absolute cap
    }

    [Fact]
    public void Twelve_hour_window_is_the_accepted_upper_bound()
    {
        var options = Resolve("Development", (AuthenticationConfiguration.SessionMinutesKey, "720"));
        Assert.Equal(TimeSpan.FromHours(12), options.ExpireTimeSpan);
    }

    [Fact]
    public void Production_refuses_cookies_without_the_secure_attribute()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            Resolve("Production", (AuthenticationConfiguration.RequireHttpsKey, "false")));

        Assert.Contains(AuthenticationConfiguration.RequireHttpsKey, exception.Message);
        Assert.Contains("A14", exception.Message);
    }

    [Fact]
    public void Production_with_secure_required_is_accepted()
    {
        var options = Resolve("Production", (AuthenticationConfiguration.RequireHttpsKey, "true"));
        Assert.Equal(CookieSecurePolicy.Always, options.Cookie.SecurePolicy);
    }
}
