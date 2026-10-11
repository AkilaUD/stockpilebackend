using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Stockpile.Api.Configuration;

namespace Stockpile.SecurityTests.Testing;

/// <summary>
/// Antiforgery for the security suite's host (plan 1.35 — the real pipeline
/// validates EVERY unsafe verb). The integration suite's `/_test/token` seam
/// lives in that assembly and is not available here, so these tests use the
/// REAL priming endpoint the frontend uses: `GET /api/v1/auth/antiforgery`
/// answers the double-submit request token in the body; the cookie is stored
/// by the client's handler and replayed alongside the header.
/// </summary>
public static class SecurityTestClient
{
    /// <summary>
    /// A client that talks to the host over TestServer's virtual HTTPS —
    /// antiforgery refuses to mint double-submit tokens over plain HTTP once
    /// the cookie is Secure, and every posting client needs the cookie
    /// replayed anyway.
    /// </summary>
    public static HttpClient HttpsClient(CredentialBudgetApiFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
        });

    /// <summary>Mints a token on <paramref name="client"/> and returns it.</summary>
    public static async Task<string> AcquireTokenAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/v1/auth/antiforgery");
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException(
                $"GET /api/v1/auth/antiforgery answered {(int)response.StatusCode}: {body}");
        }

        var payload = await response.Content.ReadFromJsonAsync<AntiforgeryPayload>();
        return payload?.RequestToken
            ?? throw new InvalidOperationException("The antiforgery endpoint returned no requestToken.");
    }

    /// <summary>Builds an unsafe request with the CSRF header attached.</summary>
    public static HttpRequestMessage UnsafePost(string path, object body, string requestToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add(AntiforgeryConfiguration.HeaderName, requestToken);
        return request;
    }

    private sealed record AntiforgeryPayload(string? RequestToken);
}
