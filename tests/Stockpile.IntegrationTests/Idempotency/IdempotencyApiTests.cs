using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Stockpile.Api.Configuration;
using Stockpile.Contracts.Errors;
using Stockpile.IntegrationTests.Testing;

namespace Stockpile.IntegrationTests.Idempotency;

/// <summary>
/// Task 1.27 — api.md §1.7 + architecture §9.4 over real HTTP, driven by the
/// test-only <c>[Idempotent]</c> endpoint: missing header → 400
/// IDEMPOTENCY_KEY_REQUIRED, same key + same body → replay with
/// <c>Idempotency-Replayed: true</c>, same key + different body → 409
/// IDEMPOTENCY_KEY_REUSE. Each test uses a fresh key (fresh Guid) so runs are
/// independent of the 7-day retention.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class IdempotencyApiTests
{
    private readonly StockpileApiFactory _factory;

    /// <summary>Shares the one API host (one migrate + seed of Stockpile_Test) across tests.</summary>
    public IdempotencyApiTests(StockpileApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Missing_header_is_400_IDEMPOTENCY_KEY_REQUIRED()
    {
        using var client = AntiforgeryTestSupport.HttpsClient(_factory);
        string token = await AntiforgeryTestSupport.AcquireTokenAsync(client);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/_test/idempotent")
        {
            Content = Body("{\"a\":1}"),
        };

        using var response = await AntiforgeryTestSupport.SendUnsafeAsync(client, request, token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var envelope = await response.Content.ReadFromJsonAsync<ErrorEnvelope>();
        Assert.NotNull(envelope);
        Assert.Equal(ErrorCodes.IdempotencyKeyRequired, envelope.Code);
    }

    [Fact]
    public async Task Malformed_key_is_400_MALFORMED_REQUEST()
    {
        using var client = AntiforgeryTestSupport.HttpsClient(_factory);
        string token = await AntiforgeryTestSupport.AcquireTokenAsync(client);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/_test/idempotent")
        {
            Content = Body("{\"a\":1}"),
        };
        request.Headers.Add("Idempotency-Key", "not-a-guid");

        using var response = await AntiforgeryTestSupport.SendUnsafeAsync(client, request, token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var envelope = await response.Content.ReadFromJsonAsync<ErrorEnvelope>();
        Assert.NotNull(envelope);
        Assert.Equal(ErrorCodes.MalformedRequest, envelope.Code);
    }

    [Fact]
    public async Task Same_key_and_body_replays_the_stored_response()
    {
        using var client = AntiforgeryTestSupport.HttpsClient(_factory);
        string token = await AntiforgeryTestSupport.AcquireTokenAsync(client);
        var key = Guid.NewGuid().ToString();

        using var first = await client.SendAsync(Post(key, "{\"a\":1}", token));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.False(first.Headers.Contains("Idempotency-Replayed"));

        using var replay = await client.SendAsync(Post(key, "{\"a\":1}", token));
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal("true", replay.Headers.GetValues("Idempotency-Replayed").Single());

        using var document = await JsonDocument.ParseAsync(await replay.Content.ReadAsStreamAsync());
        Assert.Equal("test-op-001", document.RootElement.GetProperty("reference").GetString());
        Assert.True(document.RootElement.GetProperty("replayed").GetBoolean());
    }

    [Fact]
    public async Task Same_key_different_body_is_409_IDEMPOTENCY_KEY_REUSE()
    {
        using var client = AntiforgeryTestSupport.HttpsClient(_factory);
        string token = await AntiforgeryTestSupport.AcquireTokenAsync(client);
        var key = Guid.NewGuid().ToString();

        using var first = await client.SendAsync(Post(key, "{\"a\":1}", token));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using var conflict = await client.SendAsync(Post(key, "{\"a\":2}", token));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);

        var envelope = await conflict.Content.ReadFromJsonAsync<ErrorEnvelope>();
        Assert.NotNull(envelope);
        Assert.Equal(ErrorCodes.IdempotencyKeyReuse, envelope.Code);
    }

    [Fact]
    public async Task Headers_on_unmarked_actions_are_ignored()
    {
        // A POST that is not [Idempotent] must ignore the header entirely —
        // the guard applies only to critical writes (X14).
        using var client = AntiforgeryTestSupport.HttpsClient(_factory);
        string token = await AntiforgeryTestSupport.AcquireTokenAsync(client);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/_test/echo")
        {
            Content = JsonContent.Create(new { name = "ok" }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        using var response = await AntiforgeryTestSupport.SendUnsafeAsync(client, request, token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Idempotency-Replayed"));
    }

    private static StringContent Body(string json) =>
        new(json, Encoding.UTF8, "application/json");

    private static HttpRequestMessage Post(string key, string json, string requestToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/_test/idempotent")
        {
            Content = Body(json),
        };
        request.Headers.Add("Idempotency-Key", key);
        request.Headers.Add(AntiforgeryConfiguration.HeaderName, requestToken);
        return request;
    }
}
