using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Stockpile.Api.Configuration;
using Stockpile.Contracts.Authentication;
using Stockpile.Contracts.Errors;
using Stockpile.Domain.Entities.Identity;
using Stockpile.Infrastructure.Persistence;
using Stockpile.IntegrationTests.Testing;

namespace Stockpile.IntegrationTests.Auth;

/// <summary>
/// Task 1.36 — the authentication use cases over real HTTP
/// (security-test-matrix A1/A3/A7/A10/A12/A15, BOLA, api.md §2.1): staged
/// buyer registration (201, verification pending, antiforgery minted but NO
/// session cookie), login (session + antiforgery cookies, identity payload),
/// logout / logout-all / session list / session revocation against the
/// server-side <c>LoginSession</c> rows, the SESSION_EXPIRED answer for
/// revoked/expired server sessions (A7/A15), and identical INVALID_CREDENTIALS
/// for unknown email and wrong password (A3, no enumeration).
/// <para>
/// Antiforgery follows the §1.10 flow exactly: each unsafe verb carries the
/// token minted by the response that last stored the double-submit cookie —
/// register's token feeds login, login's (fresh) token feeds logout — mirroring
/// what the SPA must do; re-minting by login/register invalidates any earlier
/// token, so the tests re-acquire exactly where a real client would.
/// </para>
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class AuthenticationApiTests
{
    private readonly StockpileApiFactory _factory;

    /// <summary>Shares the one API host (single migrate + seed of Stockpile_Test).</summary>
    public AuthenticationApiTests(StockpileApiFactory factory) => _factory = factory;

    private const string Password = "correct-horse-battery-staple";
    private const string ApiBase = "/api/v1";

    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    private static string NewEmail() => $"buyer.{Guid.NewGuid():N}@test.local";

    private static RegisterRequestPayload RegisterPayload(string email) => new(
        FirstName: "Test",
        LastName: "Buyer",
        Email: email,
        Phone: "+94771234567",
        Password);

    private static async Task<HttpResponseMessage> PostAsJsonAsync(
        HttpClient client, string route, object payload, string requestToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, route)
        {
            Content = JsonContent.Create(payload, options: Wire),
        };
        return await AntiforgeryTestSupport.SendUnsafeAsync(client, request, requestToken);
    }

    private static async Task<HttpResponseMessage> DeleteAsync(
        HttpClient client, string route, string requestToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, route);
        request.Headers.Add(AntiforgeryConfiguration.HeaderName, requestToken);
        return await client.SendAsync(request);
    }

    private static async Task<LoginPayload> ReadLoginAsync(HttpResponseMessage response)
        => await response.Content.ReadFromJsonAsync<LoginPayload>()
            ?? throw new InvalidOperationException("Login answered without a body.");

    /// <summary>
    /// The TestServer pipes <c>Set-Cookie</c> into the content headers;
    /// HttpClient exposes it on the response headers. Ask both.
    /// </summary>
    private static string[] SetCookieHeaders(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? responseCookies))
        {
            return responseCookies.ToArray();
        }

        if (response.Content.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? contentCookies))
        {
            return contentCookies.ToArray();
        }

        return Array.Empty<string>();
    }

    /// <summary>Registers and returns the client plus the register-minted request token.</summary>
    private async Task<(HttpClient Client, string RegisterToken)> RegisterAsync(string email)
    {
        HttpClient client = AntiforgeryTestSupport.HttpsClient(_factory);
        string acquired = await AntiforgeryTestSupport.AcquireTokenAsync(client);
        using var response = await PostAsJsonAsync(client, $"{ApiBase}/auth/register", RegisterPayload(email), acquired);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<RegisterResponsePayload>();
        Assert.False(string.IsNullOrWhiteSpace(body!.RequestToken), "register must mint and return a request token (§1.10)");
        return (client, body.RequestToken!);
    }

    /// <summary>Logs in on the SAME client as register (register's token is still the current one).</summary>
    private static async Task<HttpResponseMessage> LoginAsync(
        HttpClient client, string registerToken, string email)
        => await PostAsJsonAsync(client, $"{ApiBase}/auth/login", new LoginRequestPayload(email, Password), registerToken);

    /// <summary>Creates a fresh client and signs it in — a second "device" for the account.</summary>
    private async Task<(HttpClient Client, LoginPayload Body)> LoginNewClientAsync(string email)
    {
        HttpClient client = AntiforgeryTestSupport.HttpsClient(_factory);
        string acquired = await AntiforgeryTestSupport.AcquireTokenAsync(client);
        using var response = await LoginAsync(client, acquired, email);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        LoginPayload body = await ReadLoginAsync(response);
        Assert.False(string.IsNullOrWhiteSpace(body.RequestToken), "login must re-mint and return a request token (§1.10)");
        return (client, body);
    }

    // ---- Registration -------------------------------------------------------

    [Fact]
    public async Task Register_creates_the_account_mints_antiforgery_and_does_not_sign_in()
    {
        using var client = AntiforgeryTestSupport.HttpsClient(_factory);
        string token = await AntiforgeryTestSupport.AcquireTokenAsync(client);
        string email = NewEmail();

        using var response = await PostAsJsonAsync(client, $"{ApiBase}/auth/register", RegisterPayload(email), token);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("/api/v1/auth/session", response.Headers.Location?.ToString());

        var body = await response.Content.ReadFromJsonAsync<RegisterResponsePayload>();
        Assert.NotNull(body);
        Assert.Equal("verification_pending", body!.Status);

        // Registration does NOT sign the user in: no session ticket yet (api.md
        // §2.1), so the session endpoint still answers UNAUTHENTICATED.
        using (var session = await client.GetAsync($"{ApiBase}/auth/session"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, session.StatusCode);
        }

        // The account exists and can sign in — unverified, carrying the buyer
        // role. The register-minted token pairs with the double-submit cookie
        // the client handler stored, which is what makes this login valid (§1.10).
        using var login = await LoginAsync(client, body.RequestToken!, email);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        LoginPayload loginBody = await ReadLoginAsync(login);
        Assert.False(loginBody.User!.EmailConfirmed);
        Assert.Contains("buyer", loginBody.User.Roles);
    }

    [Fact]
    public async Task Register_with_a_duplicate_email_is_422_with_an_email_field_error()
    {
        using var client = AntiforgeryTestSupport.HttpsClient(_factory);
        string token = await AntiforgeryTestSupport.AcquireTokenAsync(client);
        RegisterRequestPayload payload = RegisterPayload(NewEmail());

        using (var first = await PostAsJsonAsync(client, $"{ApiBase}/auth/register", payload, token))
        {
            Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        }

        // Re-acquire: register re-minted the double-submit cookie — the old token is stale.
        string freshToken = await AntiforgeryTestSupport.AcquireTokenAsync(client);
        using var duplicate = await PostAsJsonAsync(client, $"{ApiBase}/auth/register", payload, freshToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, duplicate.StatusCode);
        var envelope = await duplicate.Content.ReadFromJsonAsync<ErrorEnvelope>();
        Assert.Equal(ErrorCodes.ValidationFailed, envelope!.Code);
        FieldError fieldError = Assert.Single(envelope.FieldErrors!);
        Assert.Equal("email", fieldError.Field);
        Assert.Equal("Duplicate", fieldError.Code);
    }

    [Fact]
    public async Task Register_with_a_weak_password_is_422_with_a_password_field_error()
    {
        // A12 — the 10-char policy (threat-model §4) rejects short passwords as
        // a field error on the password.
        using var client = AntiforgeryTestSupport.HttpsClient(_factory);
        string token = await AntiforgeryTestSupport.AcquireTokenAsync(client);

        var payload = RegisterPayload(NewEmail()) with { Password = "123456" }; // 6 < 10
        using var response = await PostAsJsonAsync(client, $"{ApiBase}/auth/register", payload, token);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ErrorEnvelope>();
        Assert.Equal(ErrorCodes.ValidationFailed, envelope!.Code);
        FieldError fieldError = Assert.Single(envelope.FieldErrors!);
        Assert.Equal("password", fieldError.Field);
        Assert.Equal("PasswordTooShort", fieldError.Code);
    }

    [Fact]
    public async Task Register_accepts_a_length_meeting_password_with_low_complexity()
    {
        // Threat-model §4: "≥ 10 chars, no composition theatre" — length is the
        // only composition rule, so a 12-char lowercase password must pass.
        using var client = AntiforgeryTestSupport.HttpsClient(_factory);
        string token = await AntiforgeryTestSupport.AcquireTokenAsync(client);

        var payload = RegisterPayload(NewEmail()) with { Password = "twelvechars!!" };
        using var response = await PostAsJsonAsync(client, $"{ApiBase}/auth/register", payload, token);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Antiforgery_minting_endpoint_primes_the_anonymous_flow()
    {
        // §1.10 — the SPA primes before its first write with GET /auth/antiforgery
        // (safe verb, no token needed); the minted token then drives registration.
        // (Cookie attributes are pinned by Antiforgery_cookie_has_the_documented_flags.)
        using var client = AntiforgeryTestSupport.HttpsClient(_factory);

        using var mint = await client.GetAsync($"{ApiBase}/auth/antiforgery");
        Assert.Equal(HttpStatusCode.OK, mint.StatusCode);

        var minted = await mint.Content.ReadFromJsonAsync<AntiforgeryTokenResponse>();
        Assert.False(string.IsNullOrWhiteSpace(minted!.RequestToken));

        // The minted token pairs with the double-submit cookie the handler just
        // stored — the proof that priming works end-to-end for an anonymous write.
        using var register = await PostAsJsonAsync(
            client, $"{ApiBase}/auth/register", RegisterPayload(NewEmail()), minted.RequestToken!);
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);
    }

    [Fact]
    public async Task Antiforgery_cookie_has_the_documented_flags()
    {
        // §1.10 — the double-submit cookie is the SPA's readable echo (HttpOnly
        // must be FALSE), SameSite=Lax, Path=/; token-issuing responses are
        // no-store. To SEE the Set-Cookie headers, this client has no jar.
        using var client = AntiforgeryTestSupport.CookieInspectClient(_factory);

        using var mint = await client.GetAsync($"{ApiBase}/auth/antiforgery");
        Assert.Equal(HttpStatusCode.OK, mint.StatusCode);

        string setCookie = Assert.Single(
            SetCookieHeaders(mint),
            cookie => cookie.StartsWith(AntiforgeryConfiguration.CookieName + "=", StringComparison.Ordinal));
        Assert.DoesNotContain("HttpOnly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SameSite=Lax", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", setCookie, StringComparison.OrdinalIgnoreCase);

        string cacheControl = mint.Headers.CacheControl?.ToString() ?? string.Empty;
        Assert.Contains("no-store", cacheControl, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Login ---------------------------------------------------------------

    [Fact]
    public async Task Login_sets_session_and_antiforgery_cookies_and_returns_the_identity()
    {
        string email = NewEmail();
        (HttpClient client, string registerToken) = await RegisterAsync(email);

        using var login = await LoginAsync(client, registerToken, email);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        // Both cookies land in the handler's jar; the token in the body is
        // freshly minted against THIS identity (§1.10 — it drives the logout and
        // revoke tests below). The session cookie is what authenticates here:
        using (var session = await client.GetAsync($"{ApiBase}/auth/session"))
        {
            Assert.Equal(HttpStatusCode.OK, session.StatusCode);
        }

        LoginPayload body = await ReadLoginAsync(login);
        Assert.Equal(email, body.User!.Email);
        Assert.Equal("buyer", Assert.Single(body.User.Roles));
        Assert.Contains("rfq.create", body.User.Permissions);
        Assert.False(body.User.EmailConfirmed);
    }

    [Fact]
    public async Task Login_with_a_wrong_password_is_401_invalid_credentials()
    {
        string email = NewEmail();
        (HttpClient client, string registerToken) = await RegisterAsync(email);

        using var login = await PostAsJsonAsync(
            client, $"{ApiBase}/auth/login",
            new LoginRequestPayload(email, "wrong-password-12345"), registerToken);

        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
        var envelope = await login.Content.ReadFromJsonAsync<ErrorEnvelope>();
        Assert.Equal(ErrorCodes.InvalidCredentials, envelope!.Code);
        Assert.Equal("Email or password is incorrect.", envelope.Message);
    }

    [Fact]
    public async Task Login_for_an_unknown_email_is_identical_to_a_wrong_password()
    {
        // A3 — no account enumeration: same status, code, message and envelope
        // shape for a non-existent account as for a wrong password.
        using var client = AntiforgeryTestSupport.HttpsClient(_factory);
        string token = await AntiforgeryTestSupport.AcquireTokenAsync(client);

        using var login = await PostAsJsonAsync(
            client, $"{ApiBase}/auth/login",
            new LoginRequestPayload(NewEmail(), "wrong-password-12345"), token);

        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
        Assert.False(login.Headers.Contains("Location"), "the API must never redirect for auth (api.md 1.4)");
        var envelope = await login.Content.ReadFromJsonAsync<ErrorEnvelope>();
        Assert.Equal(ErrorCodes.InvalidCredentials, envelope!.Code);
        Assert.Equal("Email or password is incorrect.", envelope.Message);
        Assert.NotEmpty(envelope.CorrelationId);
        Assert.NotEmpty(envelope.SupportReference);
    }

    [Fact]
    public async Task Repeated_failures_lock_the_account_and_a_good_login_is_then_rejected()
    {
        // A1 — throttled then locked: after MaxFailedAccessAttempts the account
        // answers 403 ACCOUNT_LOCKED even with the correct password.
        string email = NewEmail();
        (HttpClient client, string registerToken) = await RegisterAsync(email);

        for (int attempt = 0; attempt < 5; attempt++)
        {
            using var rejected = await PostAsJsonAsync(
                client, $"{ApiBase}/auth/login",
                new LoginRequestPayload(email, $"wrong-{attempt}"), registerToken);
            Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        }

        using var good = await LoginAsync(client, registerToken, email);
        Assert.Equal(HttpStatusCode.Forbidden, good.StatusCode);
        var envelope = await good.Content.ReadFromJsonAsync<ErrorEnvelope>();
        Assert.Equal(ErrorCodes.AccountLocked, envelope!.Code);
        Assert.Contains("locked", envelope.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Authenticated session management ------------------------------------

    [Fact]
    public async Task Authenticated_unsafe_request_without_a_token_is_400_antiforgery_failed()
    {
        // A10 — the session cookie alone cannot mutate: the double-submit token
        // is required on every unsafe verb even when authenticated.
        string email = NewEmail();
        (HttpClient client, string registerToken) = await RegisterAsync(email);
        using (var login = await LoginAsync(client, registerToken, email))
        {
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        }

        using var logout = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/auth/logout"); // no X-CSRF-TOKEN
        using var response = await client.SendAsync(logout);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ErrorEnvelope>();
        Assert.Equal(ErrorCodes.AntiforgeryFailed, envelope!.Code);

        // The missing token cost nothing: the session is still live.
        using var session = await client.GetAsync($"{ApiBase}/auth/session");
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);
    }

    [Fact]
    public async Task Logout_revokes_the_server_session_and_clears_the_cookie()
    {
        string email = NewEmail();
        (HttpClient client, string registerToken) = await RegisterAsync(email);
        using var loginResponse = await LoginAsync(client, registerToken, email);
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        LoginPayload loginBody = await ReadLoginAsync(loginResponse);

        using var logout = await PostAsJsonAsync(client, $"{ApiBase}/auth/logout", new { }, loginBody.RequestToken!);
        string logoutBodyText = await logout.Content.ReadAsStringAsync();
        Assert.True(logout.StatusCode == HttpStatusCode.NoContent, $"logout {logout.StatusCode}: {logoutBodyText}");

        // Cookie cleared → anonymous on this device.
        using (var session = await client.GetAsync($"{ApiBase}/auth/session"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, session.StatusCode);
            var envelope = await session.Content.ReadFromJsonAsync<ErrorEnvelope>();
            Assert.Equal(ErrorCodes.Unauthenticated, envelope!.Code);
        }

        // …and the server rows are revoked — a replayed ticket would answer
        // SESSION_EXPIRED (A7), because revocation is not client-side only.
        await AssertAllSessionsRevokedAsync(email);

        // The 204 carries no body, so after an identity change the SPA re-primes
        // (§1.10): a fresh anonym mint on this device feeds the next login.
        string reprimed = await AntiforgeryTestSupport.AcquireTokenAsync(client);
        using var reLogin = await LoginAsync(client, reprimed, email);
        Assert.Equal(HttpStatusCode.OK, reLogin.StatusCode);
    }

    [Fact]
    public async Task Logout_all_invalidates_every_session()
    {
        // A15 — sign-out on all devices: this ticket is cleared, and the other
        // device's still-present cookie is rejected against the revoked rows.
        string email = NewEmail();
        (HttpClient clientA, string registerToken) = await RegisterAsync(email);
        using var loginResponse = await LoginAsync(clientA, registerToken, email);
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        LoginPayload loginA = await ReadLoginAsync(loginResponse);
        (HttpClient clientB, _) = await LoginNewClientAsync(email);

        using var all = await PostAsJsonAsync(clientA, $"{ApiBase}/auth/logout-all", new { }, loginA.RequestToken!);
        Assert.Equal(HttpStatusCode.NoContent, all.StatusCode);

        using (var sessionA = await clientA.GetAsync($"{ApiBase}/auth/session"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, sessionA.StatusCode); // cookie cleared
        }

        using var sessionB = await clientB.GetAsync($"{ApiBase}/auth/session");
        Assert.Equal(HttpStatusCode.Unauthorized, sessionB.StatusCode);
        var envelopeB = await sessionB.Content.ReadFromJsonAsync<ErrorEnvelope>();
        Assert.Equal(ErrorCodes.SessionExpired, envelopeB!.Code); // revoked server row (A15)

        await AssertAllSessionsRevokedAsync(email);
    }

    [Fact]
    public async Task Session_list_shows_all_active_sessions_with_the_current_marked()
    {
        string email = NewEmail();
        (HttpClient clientA, string registerToken) = await RegisterAsync(email);
        using (var login = await LoginAsync(clientA, registerToken, email))
        {
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        }
        await LoginNewClientAsync(email);
        (HttpClient clientB, _) = await LoginNewClientAsync(email);

        using var response = await clientB.GetAsync($"{ApiBase}/auth/sessions");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var list = await response.Content.ReadFromJsonAsync<SessionsListResponse>();

        Assert.NotNull(list);
        Assert.Equal(3, list!.Items.Count);
        ActiveSessionResponse current = Assert.Single(list.Items, item => item.IsCurrent);
        Assert.Equal(list.CurrentSessionId, current.Id);
        Assert.True(Guid.TryParse(current.Id, out _), "session ids are the LoginSession Guids");
        Assert.All(list.Items, item =>
        {
            Assert.True(Guid.TryParse(item.Id, out _));
            Assert.True(item.IssuedAt <= item.LastSeenAt);
            Assert.True(item.ExpiresAt > item.LastSeenAt);
        });
    }

    [Fact]
    public async Task Revoking_a_session_from_the_list_kills_that_device()
    {
        string email = NewEmail();
        (HttpClient clientA1, string registerToken) = await RegisterAsync(email);
        using var loginResponse = await LoginAsync(clientA1, registerToken, email);
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        LoginPayload loginA1 = await ReadLoginAsync(loginResponse);
        (HttpClient clientA2, _) = await LoginNewClientAsync(email);

        using var list = await clientA1.GetAsync($"{ApiBase}/auth/sessions");
        var sessions = await list.Content.ReadFromJsonAsync<SessionsListResponse>();
        Assert.Equal(2, sessions!.Items.Count);
        string otherSessionId = Assert.Single(sessions.Items, item => !item.IsCurrent).Id;

        using var revoke = await DeleteAsync(clientA1, $"{ApiBase}/auth/sessions/{otherSessionId}", loginA1.RequestToken!);
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);

        // The revoked device can no longer authenticate — SESSION_EXPIRED.
        using var sessionA2 = await clientA2.GetAsync($"{ApiBase}/auth/session");
        Assert.Equal(HttpStatusCode.Unauthorized, sessionA2.StatusCode);
        var envelope = await sessionA2.Content.ReadFromJsonAsync<ErrorEnvelope>();
        Assert.Equal(ErrorCodes.SessionExpired, envelope!.Code);

        // The remaining device still lists exactly one session.
        using var after = await clientA1.GetAsync($"{ApiBase}/auth/sessions");
        var remaining = await after.Content.ReadFromJsonAsync<SessionsListResponse>();
        Assert.Single(remaining!.Items);
    }

    [Fact]
    public async Task Revoking_a_foreign_or_unknown_session_answers_404_without_leaking_existence()
    {
        // BOLA (api.md §1.6): another user's session id — and a random one —
        // both answer 404 RESOURCE_NOT_FOUND; existence is never leaked.
        string emailA = NewEmail();
        (HttpClient clientA, string registerTokenA) = await RegisterAsync(emailA);
        using var loginAResponse = await LoginAsync(clientA, registerTokenA, emailA);
        Assert.Equal(HttpStatusCode.OK, loginAResponse.StatusCode);
        LoginPayload loginASession = await ReadLoginAsync(loginAResponse);

        string emailB = NewEmail();
        (HttpClient clientB, string registerTokenB) = await RegisterAsync(emailB);
        using (var loginB = await LoginAsync(clientB, registerTokenB, emailB)) { Assert.Equal(HttpStatusCode.OK, loginB.StatusCode); }
        (HttpClient clientB2, _) = await LoginNewClientAsync(emailB);

        using (var listB = await clientB2.GetAsync($"{ApiBase}/auth/sessions"))
        {
            var theirs = await listB.Content.ReadFromJsonAsync<SessionsListResponse>();
            Assert.NotNull(theirs);
            string foreignId = theirs!.Items[0].Id; // user B has ≥1 live session of its own; A must not reach it

            using var foreign = await DeleteAsync(clientA, $"{ApiBase}/auth/sessions/{foreignId}", loginASession.RequestToken!);
            Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
            var envelope = await foreign.Content.ReadFromJsonAsync<ErrorEnvelope>();
            Assert.Equal(ErrorCodes.ResourceNotFound, envelope!.Code);
        }

        using var unknown = await DeleteAsync(clientA, $"{ApiBase}/auth/sessions/{Guid.NewGuid():N}", loginASession.RequestToken!);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task An_expired_server_session_answers_401_session_expired()
    {
        // A7 — the absolute-cap row expiry: even with a live-looking ticket the
        // server row says the session is over, and the answer is SESSION_EXPIRED.
        string email = NewEmail();
        (HttpClient client, string registerToken) = await RegisterAsync(email);
        using (var login = await LoginAsync(client, registerToken, email)) { Assert.Equal(HttpStatusCode.OK, login.StatusCode); }

        Guid sessionId;
        using (var list = await client.GetAsync($"{ApiBase}/auth/sessions"))
        {
            var sessions = await list.Content.ReadFromJsonAsync<SessionsListResponse>();
            sessionId = Guid.Parse(sessions!.CurrentSessionId);
        }

        await ExpireSessionAsync(sessionId);

        using var session = await client.GetAsync($"{ApiBase}/auth/session");
        Assert.Equal(HttpStatusCode.Unauthorized, session.StatusCode);
        var envelope = await session.Content.ReadFromJsonAsync<ErrorEnvelope>();
        Assert.Equal(ErrorCodes.SessionExpired, envelope!.Code);
    }

    [Fact]
    public async Task Session_payload_includes_roles_permissions_and_membership_shape()
    {
        string email = NewEmail();
        (HttpClient client, string registerToken) = await RegisterAsync(email);
        using (var login = await LoginAsync(client, registerToken, email)) { Assert.Equal(HttpStatusCode.OK, login.StatusCode); }

        using var response = await client.GetAsync($"{ApiBase}/auth/session");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var identity = await response.Content.ReadFromJsonAsync<AuthSessionResponse>();

        Assert.NotNull(identity);
        Assert.Equal(email, identity!.Email);
        Assert.Equal("Test", identity.FirstName);
        Assert.Equal("buyer", Assert.Single(identity.Roles));
        Assert.Contains("rfq.create", identity.Permissions);
        Assert.Contains("rfq.submit", identity.Permissions);
        Assert.Empty(identity.OrgMemberships); // the wire shape is reserved; memberships arrive with 1.43
        Assert.True(Guid.TryParse(identity.PublicId, out _));
    }

    // ---- DB-level helpers -----------------------------------------------------

    private async Task ExpireSessionAsync(Guid sessionId)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StockpileDbContext>();
        LoginSession session = await db.LoginSessions.FirstAsync(row => row.Id == sessionId);
        session.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
    }

    private async Task AssertAllSessionsRevokedAsync(string email)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StockpileDbContext>();
        // EmailNormalized follows the Identity default normalizer (UpperInvariant);
        // the predicate must translate to SQL — the pragma is the codebase
        // convention for EF-translatable UPPER comparisons (see StockpileRoleStore).
#pragma warning disable CA1862 // `ToUpperInvariant()` translates to SQL UPPER(); StringComparison overloads do not
        User user = await db.Users.FirstAsync(
            row => row.EmailNormalized == email.ToUpperInvariant());
#pragma warning restore CA1862
        List<LoginSession> sessions =
            await db.LoginSessions.Where(row => row.UserId == user.PublicId).ToListAsync();
        Assert.NotEmpty(sessions);
        Assert.All(sessions, row => Assert.NotNull(row.RevokedAt));
    }

    // ---- Wire shapes -----------------------------------------------------------

    private sealed record RegisterRequestPayload(
        string FirstName, string LastName, string Email, string? Phone, string Password);

    private sealed record LoginRequestPayload(string Email, string Password);

    private sealed record RegisterResponsePayload(string? Status, string? RequestToken);

    private sealed record LoginPayload(string? RequestToken, AuthSessionResponse? User);
}