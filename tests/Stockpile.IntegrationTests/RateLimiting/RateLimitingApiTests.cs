using System.Net;
using System.Net.Http.Json;
using Stockpile.Contracts.Errors;
using Stockpile.IntegrationTests.Testing;

namespace Stockpile.IntegrationTests.RateLimiting;

/// <summary>
/// Task 1.26 over real HTTP — a configured policy throttles in minutes, other
/// endpoints stay unthrottled, and the 429 carries the api.md §1.8 contract:
/// envelope (RATE_LIMITED, retryable, support reference) plus the four headers
/// (Retry-After, X-RateLimit-Limit/-Remaining/-Reset). The test-only "test.tight"
/// policy (2 / min) is registered by Program only under the Testing
/// environment and attached to the test-only endpoint.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class RateLimitingApiTests
{
    private readonly StockpileApiFactory _factory;

    /// <summary>Shares the one API host (one migrate + seed of Stockpile_Test) across tests.</summary>
    public RateLimitingApiTests(StockpileApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Third_request_within_the_window_is_429_with_headers_and_envelope()
    {
        using var client = _factory.CreateClient();

        var allowed = await client.GetAsync("/_test/rate-limit");
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        allowed = await client.GetAsync("/_test/rate-limit");
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);

        using var rejected = await client.GetAsync("/_test/rate-limit");
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);

        // api.md §1.8 headers.
        Assert.Equal("2", rejected.Headers.GetValues("X-RateLimit-Limit").Single());
        Assert.Equal("0", rejected.Headers.GetValues("X-RateLimit-Remaining").Single());
        Assert.True(rejected.Headers.Contains("Retry-After"));
        Assert.True(rejected.Headers.TryGetValues("X-RateLimit-Reset", out var reset));
        Assert.False(string.IsNullOrWhiteSpace(Assert.Single(reset!)));

        // api.md §1.4/§1.5 envelope — RATE_LIMITED, retryable, correlated.
        var envelope = await rejected.Content.ReadFromJsonAsync<ErrorEnvelope>();
        Assert.NotNull(envelope);
        Assert.Equal(ErrorCodes.RateLimited, envelope.Code);
        Assert.True(envelope.Retryable);
        Assert.False(string.IsNullOrWhiteSpace(envelope.SupportReference));
        Assert.False(string.IsNullOrWhiteSpace(envelope.CorrelationId));

        // The limiter neither throttles undeclared endpoints nor wedges the
        // middleware for later requests.
        Assert.Equal(
            HttpStatusCode.OK,
            (await client.GetAsync("/api/v1/health/live")).StatusCode);
    }

    [Fact]
    public async Task Health_endpoint_is_not_throttled()
    {
        using var client = _factory.CreateClient();

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(
                HttpStatusCode.OK,
                (await client.GetAsync("/api/v1/health/live")).StatusCode);
        }
    }
}
