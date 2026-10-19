using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Stockpile.Api.Configuration;
using Stockpile.Contracts.Errors;
using Stockpile.IntegrationTests.Testing;

namespace Stockpile.IntegrationTests.ReverseProxy;

/// <summary>
/// Threat-model §18 over the real pipeline — the retrofit that lets a deployed
/// API whose origin hop is plain HTTP tell the truth about itself (the Vercel
/// edge terminates TLS and forwards
/// <c>X-Forwarded-Proto</c>/<c>X-Forwarded-For</c>/<c>X-Forwarded-Host</c>).
/// Without it, antiforgery token minting threw
/// (<c>DefaultAntiforgery.CheckSSLConfig</c>: "SecurePolicy = Always, but the
/// current request is not an SSL request"), so sign-in and registration — both
/// of which mint first — answered 500 on the deployed host.
/// <para>
/// The suite host runs with <c>Authentication:RequireHttps=true</c> (as
/// Production must) and declares "any address" as the edge, mirroring a
/// deployment whose proxy range cannot be enumerated by address. Clients here
/// speak plain HTTP unless a test needs to replay the Secure antiforgery
/// cookie, in which case it uses TestServer's virtual HTTPS (TestServer serves
/// it without real TLS) and only the forwarded HOST is in play.
/// </para>
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class ReverseProxyApiTests
{
    private const string PublicHost = "stockpilefrontend.vercel.app";
    private const string VisitorAddress = "203.0.113.50";

    private readonly StockpileApiFactory _factory;

    /// <summary>Shares the one API host (single migrate + seed of Stockpile_Test).</summary>
    public ReverseProxyApiTests(StockpileApiFactory factory) => _factory = factory;

    /// <summary>Plain HTTP (TestServer's virtual http://localhost), cookie jar attached.</summary>
    private HttpClient PlainClient() => _factory.CreateClient();

    /// <summary>Plain HTTP without a cookie jar, so Set-Cookie attributes surface.</summary>
    private HttpClient CookieInspectingClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

    /// <summary>The edge's claim for a visitor who used https on the public host.</summary>
    private static void DeclarePublicEdge(HttpRequestMessage request)
    {
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.Add("X-Forwarded-Host", PublicHost);
        request.Headers.Add("X-Forwarded-For", VisitorAddress);
    }

    [Fact]
    public async Task AntiforgeryMinting_OverPlainHttp_WithTheDeclaredEdgeSayingHttps_Answers200()
    {
        using HttpClient client = PlainClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/antiforgery");
        DeclarePublicEdge(request);

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(string.IsNullOrWhiteSpace(
            json.RootElement.GetProperty("requestToken").GetString()));
    }

    [Fact]
    public async Task AntiforgeryCookie_WhenTheEdgeSaysHttps_CarriesSecure()
    {
        using HttpClient client = CookieInspectingClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/antiforgery");
        DeclarePublicEdge(request);

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string cookie = Assert.Single(SetCookieValues(response), value =>
            value.StartsWith(AntiforgeryConfiguration.CookieName, StringComparison.Ordinal));
        string attributes = cookie[(cookie.IndexOf(';') + 1)..];

        // The public surface is HTTPS, so the token that authorises every unsafe
        // verb is never issued without Secure — even though the hop this request
        // arrived on is not (A14's flag discipline, security-test-matrix A14).
        Assert.Contains("secure", attributes, StringComparison.OrdinalIgnoreCase);

        // HttpOnly stays off by design (AntiforgeryConfiguration): the browser
        // app must read the claim to echo it as the double-submit header.
        Assert.DoesNotContain("httponly", attributes, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WithoutAForwardedScheme_PlainHttpStillRefusesToMint()
    {
        using HttpClient client = PlainClient();

        using HttpResponseMessage response = await client.GetAsync("/api/v1/auth/antiforgery");

        // Cookie.SecurePolicy = Always over a request that really is not HTTPS:
        // DefaultAntiforgery.CheckSSLConfig refuses. The remedy is teaching the
        // API the public scheme (above), never relaxing the policy.
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task Pipeline_AdoptsTheForwardedSchemeHostAndVisitorAddress()
    {
        using HttpClient client = PlainClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/_test/connection");
        DeclarePublicEdge(request);

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement view = json.RootElement;
        Assert.Equal("https", view.GetProperty("scheme").GetString());
        Assert.True(view.GetProperty("isHttps").GetBoolean());
        Assert.Equal(PublicHost, view.GetProperty("host").GetString());
        // Rate-limit partitions (RateLimitPartitionKeys) and audit rows key on
        // this address: without the retrofit every visitor behind the edge
        // shared the proxy's bucket.
        Assert.Equal(VisitorAddress, view.GetProperty("remoteIp").GetString());
    }

    [Fact]
    public async Task AntiforgeryOriginCheck_UsesTheForwardedPublicHost()
    {
        using HttpClient client = AntiforgeryTestSupport.HttpsClient(_factory);
        string token = await MintAsync(client, declareEdge: true);

        using HttpResponseMessage response = await PostEchoAsync(
            client, token, "https://" + PublicHost, declareEdge: true);

        // The Origin matches the request's own scheme and (forwarded) host, so a
        // POST issued by the browser app on the Vercel origin survives the C4
        // origin check.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AntiforgeryOriginCheck_RejectsAForeignOriginEvenWithTheForwardedHost()
    {
        using HttpClient client = AntiforgeryTestSupport.HttpsClient(_factory);
        string token = await MintAsync(client, declareEdge: true);

        using HttpResponseMessage response = await PostEchoAsync(
            client, token, "https://evil.test", declareEdge: true);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertAntiforgeryFailedAsync(response);
    }

    [Fact]
    public async Task AntiforgeryOriginCheck_WithoutTheForwardedHost_RejectsThePublicOrigin()
    {
        // The forwarded host is load-bearing, not decoration: the same public
        // origin is refused while the API still believes its host is the origin
        // hop's — which is exactly the state a deployed API is in before this
        // retrofit.
        using HttpClient client = AntiforgeryTestSupport.HttpsClient(_factory);
        string token = await MintAsync(client, declareEdge: false);

        using HttpResponseMessage response = await PostEchoAsync(
            client, token, "https://" + PublicHost, declareEdge: false);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertAntiforgeryFailedAsync(response);
    }

    private static async Task<string> MintAsync(HttpClient client, bool declareEdge)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/antiforgery");
        if (declareEdge)
        {
            DeclarePublicEdge(request);
        }

        using HttpResponseMessage response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("requestToken").GetString()!;
    }

    private static async Task<HttpResponseMessage> PostEchoAsync(
        HttpClient client, string requestToken, string origin, bool declareEdge)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/_test/echo")
        {
            Content = JsonContent.Create(new { name = "Ok" }),
        };
        request.Headers.Add(AntiforgeryConfiguration.HeaderName, requestToken);
        request.Headers.Add("Origin", origin);
        if (declareEdge)
        {
            DeclarePublicEdge(request);
        }

        return await client.SendAsync(request);
    }

    private static async Task AssertAntiforgeryFailedAsync(HttpResponseMessage response)
    {
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(ErrorCodes.AntiforgeryFailed, json.RootElement.GetProperty("code").GetString());
    }

    private static IEnumerable<string> SetCookieValues(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? values) ? values : [];
}
