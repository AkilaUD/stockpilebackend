using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Stockpile.Api.Configuration;
using Stockpile.Contracts.Errors;
using Stockpile.IntegrationTests.Testing;

namespace Stockpile.IntegrationTests.Auth;

/// <summary>
/// Task 1.34 — cookie authentication over real HTTP (security-test-matrix
/// A14): a sign-in response carries the session cookie with HttpOnly,
/// SameSite=Lax and (the host enforces Authentication:RequireHttps) Secure;
/// replaying the cookie authenticates subsequent requests; the ticket is
/// cleared on sign-out; and the cookie handler's "redirects" surface as
/// 401 + UNAUTHENTICATED JSON — the API never answers auth with a Location.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class CookieAuthenticationApiTests
{
    private readonly StockpileApiFactory _factory;

    /// <summary>Shares the one API host (single migrate + seed of Stockpile_Test).</summary>
    public CookieAuthenticationApiTests(StockpileApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Sign_in_issues_the_A14_cookie_and_the_ticket_survives_replay()
    {
        // HTTPS base address so the Secure cookie is replayed (the TestServer
        // serves https://virtual requests without real TLS).
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
        });

        using (var signIn = await client.GetAsync("/_test/signin"))
        {
            Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);

            string setCookie = Assert.Single(signIn.Headers.GetValues("Set-Cookie"));
            Assert.StartsWith(AuthenticationConfiguration.CookieName + "=", setCookie, StringComparison.Ordinal);
            Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);   // A14
            Assert.Contains("samesite=lax", setCookie, StringComparison.OrdinalIgnoreCase); // A14
            Assert.Contains("secure", setCookie, StringComparison.OrdinalIgnoreCase);     // A14 (RequireHttps)
        }

        using (var whoAmI = await client.GetAsync("/_test/whoami"))
        {
            Assert.Equal(HttpStatusCode.OK, whoAmI.StatusCode);
            var body = await whoAmI.Content.ReadFromJsonAsync<WhoAmIResponse>();
            Assert.True(body!.Authenticated);
            Assert.Equal(TestOnlySubjects.Name, body.Name);
        }

        using var protectedResponse = await client.GetAsync("/_test/protected");
        Assert.Equal(HttpStatusCode.OK, protectedResponse.StatusCode);
        var protectedBody = await protectedResponse.Content.ReadFromJsonAsync<ProtectedResponse>();
        Assert.Equal(TestOnlySubjects.Name, protectedBody!.Name);
    }

    [Fact]
    public async Task Sign_out_clears_the_cookie_and_kills_the_ticket()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
        });

        await client.GetAsync("/_test/signin");
        string token = await AntiforgeryTestSupport.AcquireTokenAsync(client);
        using var signOutRequest = new HttpRequestMessage(HttpMethod.Post, "/_test/signout");
        using var signOut = await AntiforgeryTestSupport.SendUnsafeAsync(client, signOutRequest, token);
        Assert.Equal(HttpStatusCode.OK, signOut.StatusCode);

        // The handler expires the cookie immediately (past Expires / Max-Age 0).
        string setCookie = Assert.Single(signOut.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith(AuthenticationConfiguration.CookieName + "=", setCookie, StringComparison.Ordinal);

        using (var whoAmI = await client.GetAsync("/_test/whoami"))
        {
            var body = await whoAmI.Content.ReadFromJsonAsync<WhoAmIResponse>();
            Assert.False(body!.Authenticated);
        }

        using var protectedResponse = await client.GetAsync("/_test/protected");
        Assert.Equal(HttpStatusCode.Unauthorized, protectedResponse.StatusCode);
    }

    [Fact]
    public async Task Unauthenticated_protected_request_is_401_json_not_a_redirect()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/_test/protected");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(response.Headers.Contains("Location"), "the API must never redirect for auth (api.md 1.4)");
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        var envelope = await response.Content.ReadFromJsonAsync<ErrorEnvelope>();
        Assert.Equal(ErrorCodes.Unauthenticated, envelope!.Code);
        Assert.NotEmpty(envelope.SupportReference);
        Assert.NotEmpty(envelope.CorrelationId);
    }

    private sealed record WhoAmIResponse(bool Authenticated, string? Name);

    private sealed record ProtectedResponse(string Name);
}
