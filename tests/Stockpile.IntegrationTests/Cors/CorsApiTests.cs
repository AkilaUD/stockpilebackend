using System.Net;
using Stockpile.IntegrationTests.Testing;

namespace Stockpile.IntegrationTests.Cors;

/// <summary>
/// Task 1.25 over real HTTP — security-matrix §5: C6 unlisted origin gets no
/// Access-Control-Allow-Origin; C8 an allowlisted origin is echoed with
/// credentials; preflights are answered by the policy. The factory's Testing
/// environment allowlists exactly one origin (Cors__AllowedOrigins__0 =
/// https://allowed.test) so both sides of the default-deny policy are
/// observable.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class CorsApiTests
{
    private readonly StockpileApiFactory _factory;

    /// <summary>Shares the one API host (one migrate + seed of Stockpile_Test) across tests.</summary>
    public CorsApiTests(StockpileApiFactory factory) => _factory = factory;

    [Fact]
    public async Task C6_Unlisted_origin_gets_no_access_control_headers()
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/health/live");
        request.Headers.Add("Origin", "https://evil.example");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
    }

    [Fact]
    public async Task C8_Allowed_origin_is_echoed_with_credentials()
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/health/live");
        request.Headers.Add("Origin", "https://allowed.test");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.TryGetValues(
            "Access-Control-Allow-Origin", out var origins));
        Assert.Equal("https://allowed.test", Assert.Single(origins!));
        Assert.True(response.Headers.TryGetValues(
            "Access-Control-Allow-Credentials", out var credentials));
        Assert.Equal("true", Assert.Single(credentials!));

        // Actual responses (not preflights) expose the correlation header so
        // a cross-origin caller can read it (threat-model §6). ASP.NET Core
        // joins the exposed headers into one comma-separated value.
        Assert.True(response.Headers.TryGetValues(
            "Access-Control-Expose-Headers", out var exposed));
        var exposedHeaders = string.Join(",", exposed!)
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("X-Correlation-Id", exposedHeaders);
        Assert.Contains("X-RateLimit-Reset", exposedHeaders);
        Assert.Contains("Retry-After", exposedHeaders);
    }

    [Fact]
    public async Task Preflight_for_allowed_origin_returns_methods_and_exposed_headers()
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/v1/health/live");
        request.Headers.Add("Origin", "https://allowed.test");
        request.Headers.Add("Access-Control-Request-Method", "GET");

        using var response = await client.SendAsync(request);

        Assert.InRange((int)response.StatusCode, 200, 299);
        Assert.True(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.True(response.Headers.Contains("Access-Control-Allow-Credentials"));
        Assert.True(response.Headers.TryGetValues(
            "Access-Control-Allow-Methods", out _));
        // Access-Control-Expose-Headers is an actual-response header (CORS
        // fetch spec), not a preflight one — asserted in C8 below.
    }

    [Fact]
    public async Task Preflight_for_unlisted_origin_is_denied()
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/v1/health/live");
        request.Headers.Add("Origin", "https://evil.example");
        request.Headers.Add("Access-Control-Request-Method", "GET");

        using var response = await client.SendAsync(request);

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.False(response.Headers.Contains("Access-Control-Allow-Methods"));
    }
}
