using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Stockpile.Api.Configuration;

namespace Stockpile.IntegrationTests.Testing;

/// <summary>
/// Task 1.35 — the one place that knows about antiforgery inside the test
/// suite. The shared API host validates EVERY unsafe verb (plan 1.35), so each
/// client must first fetch a double-submit token from the <c>/_test/token</c>
/// seam and attach it to every POST/PUT/PATCH/DELETE. The token cookie is
/// stored by the client's cookie handler on that first GET and replayed with
/// the header.
/// </summary>
public static class AntiforgeryTestSupport
{
    /// <summary>
    /// Gets a valid token for <paramref name="client"/> (storing the
    /// double-submit cookie on the same handler) and returns the request token
    /// to echo in <see cref="AntiforgeryConfiguration.HeaderName"/>.
    /// </summary>
    public static async Task<string> AcquireTokenAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/_test/token");
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException(
                $"The /_test/token seam answered {(int)response.StatusCode}: {body}");
        }

        var token = await response.Content.ReadFromJsonAsync<AntiforgeryTokenPayload>();
        return token?.RequestToken
            ?? throw new InvalidOperationException("The test seam returned no antiforgery request token.");
    }

    /// <summary>
    /// A client that talks to the shared host over TestServer's virtual HTTPS.
    /// Antiforgery's <c>CheckSSLConfig</c> refuses to mint double-submit tokens
    /// over plain HTTP once <c>Cookie.SecurePolicy = Always</c> (which the
    /// factory forces to mirror production), so every client that posts must
    /// mint over https. The session cookie is also Secure, so authenticated
    /// flows need it anyway (architecture §8.1).
    /// </summary>
    public static HttpClient HttpsClient(StockpileApiFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
        });

    /// <summary>
    /// Like <see cref="HttpsClient"/> but WITHOUT a cookie jar, so the server's
    /// <c>Set-Cookie</c> headers surface on the response instead of being
    /// consumed. Use only for inspecting cookie attributes (§1.10); a jar-less
    /// client cannot replay cookies and therefore cannot pass antiforgery.
    /// </summary>
    public static HttpClient CookieInspectClient(StockpileApiFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = false,
        });

    /// <summary>Attaches the request token to an unsafe request before sending.</summary>
    public static async Task<HttpResponseMessage> SendUnsafeAsync(
        HttpClient client, HttpRequestMessage request, string requestToken)
    {
        request.Headers.Add(AntiforgeryConfiguration.HeaderName, requestToken);
        return await client.SendAsync(request);
    }

    private sealed record AntiforgeryTokenPayload(string? RequestToken);
}
