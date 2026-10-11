using System.Net;
using System.Net.Http.Json;
using Stockpile.Api.Configuration;
using Stockpile.Contracts.Errors;
using Stockpile.IntegrationTests.Testing;

namespace Stockpile.IntegrationTests.Antiforgery;

/// <summary>
/// Task 1.35 — security-matrix §4 (C1–C5) over real HTTP. The shared host
/// validates antiforgery on EVERY unsafe verb: no token (C1), a tampered
/// "stale" token (C2), a token from another session's cookie (C3), and a
/// foreign Origin even with a valid token (C4) are all rejected with
/// 400 + ANTIFORGERY_FAILED envelopes; a GET on a write-only route is 405 (C5).
/// The https://localhost base address replays the Secure antiforgery cookie
/// (TestServer serves virtual HTTPS without real TLS).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class AntiforgeryApiTests
{
    private readonly StockpileApiFactory _factory;

    /// <summary>Shares the one API host (single migrate + seed of Stockpile_Test).</summary>
    public AntiforgeryApiTests(StockpileApiFactory factory) => _factory = factory;

    private HttpClient HttpsClient() => AntiforgeryTestSupport.HttpsClient(_factory);

    private static async Task<HttpResponseMessage> PostEchoAsync(
        HttpClient client, string? requestToken, string? origin = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/_test/echo")
        {
            Content = JsonContent.Create(new { name = "Ok" }),
        };
        if (requestToken is not null)
        {
            request.Headers.Add(AntiforgeryConfiguration.HeaderName, requestToken);
        }

        if (origin is not null)
        {
            request.Headers.Add("Origin", origin);
        }

        return await client.SendAsync(request);
    }

    private static async Task AssertAntiforgeryRejectedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var envelope = await response.Content.ReadFromJsonAsync<ErrorEnvelope>();
        Assert.Equal(ErrorCodes.AntiforgeryFailed, envelope!.Code);
        Assert.NotEmpty(envelope.CorrelationId);
        Assert.NotEmpty(envelope.SupportReference);
    }

    [Fact]
    public async Task C1_unsafe_verb_without_a_token_is_rejected()
    {
        using var client = HttpsClient();
        using var response = await PostEchoAsync(client, requestToken: null);
        await AssertAntiforgeryRejectedAsync(response);
    }

    [Fact]
    public async Task C2_tampered_token_is_rejected()
    {
        using var client = HttpsClient();
        string valid = await AntiforgeryTestSupport.AcquireTokenAsync(client);
        char[] chars = valid.ToCharArray();
        chars[^1] = chars[^1] == 'A' ? 'B' : 'A';

        using var response = await PostEchoAsync(client, new string(chars));
        await AssertAntiforgeryRejectedAsync(response);
    }

    [Fact]
    public async Task C3_token_from_another_session_is_rejected()
    {
        // The attacker owns a valid session (own cookie + token); the victim
        // never sent that token, so the double-submit claim cannot match.
        using var attacker = HttpsClient();
        string stolenToken = await AntiforgeryTestSupport.AcquireTokenAsync(attacker);

        using var victim = HttpsClient();
        await AntiforgeryTestSupport.AcquireTokenAsync(victim); // its own cookie is now stored

        using var response = await PostEchoAsync(victim, stolenToken);
        await AssertAntiforgeryRejectedAsync(response);
    }

    [Fact]
    public async Task C4_foreign_origin_is_rejected_even_with_a_valid_token()
    {
        using var client = HttpsClient();
        string validToken = await AntiforgeryTestSupport.AcquireTokenAsync(client);

        using var response = await PostEchoAsync(client, validToken, origin: "https://evil.example");
        await AssertAntiforgeryRejectedAsync(response);
    }

    [Fact]
    public async Task C5_get_on_a_write_route_is_405()
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/_test/idempotent");
        // Routing answers 405 before any controller code runs — the state
        // change simply cannot ride a GET (C5).
        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task Valid_token_and_matching_origin_passes_through()
    {
        using var client = HttpsClient();
        string token = await AntiforgeryTestSupport.AcquireTokenAsync(client);

        using var response = await PostEchoAsync(client, token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<TestOnlyRequest>();
        Assert.Equal("Ok", body!.Name);
    }
}
