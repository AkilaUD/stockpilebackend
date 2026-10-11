using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Stockpile.Api.Configuration;
using Stockpile.Contracts.Authentication;
using Stockpile.Contracts.Errors;
using Stockpile.Domain.Entities.Identity;
using Stockpile.Domain.Entities.Platform;
using Stockpile.Domain.Enums;
using Stockpile.Infrastructure.Persistence;
using Stockpile.IntegrationTests.Testing;

namespace Stockpile.IntegrationTests.Auth;

/// <summary>
/// Task 1.40 — refresh rotation with reuse detection over real HTTP
/// (security-test-matrix A8/A9, threat-model T1.4, architecture §8.1): login
/// issues the second HttpOnly cookie, <c>POST /auth/refresh</c> advances the
/// chain (old row → <c>RotatedToId</c> → successor) and re-establishes the
/// session, a replayed (already-rotated) head revokes the WHOLE chain plus its
/// session and raises <c>RefreshReuseDetected</c> (A9), and every other dead
/// state — logout-all (A8), expiry, unknown — answers one identical 401
/// <c>SESSION_EXPIRED</c>. The Testing host runs
/// <c>Auth:RefreshTokenMinutes=0.17</c> (10.2 s), so the expiry test drives the
/// real <c>RefreshSessions</c> row expiry; replays bypass the client's cookie
/// jar with a hand-built Cookie header, because the jar would have replaced the
/// saved token with its rotated successor.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class RefreshSessionApiTests
{
    private readonly StockpileApiFactory _factory;

    /// <summary>Shares the one API host (single migrate + seed of Stockpile_Test).</summary>
    public RefreshSessionApiTests(StockpileApiFactory factory) => _factory = factory;

    private const string Password = "correct-horse-battery-staple";
    private const string ApiBase = "/api/v1";

    /// <summary>
    /// Past the Testing refresh TTL (0.17 min = 10.2 s) with margin — sleeping
    /// this long guarantees the persisted row expired, whatever else runs.
    /// </summary>
    private static readonly TimeSpan PastRefreshTtl = TimeSpan.FromSeconds(11.5);

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

    /// <summary>Reads the shared auth payload shape — login and refresh answer the same body.</summary>
    private static async Task<AuthPayload> ReadAuthPayloadAsync(HttpResponseMessage response)
        => await response.Content.ReadFromJsonAsync<AuthPayload>()
            ?? throw new InvalidOperationException("The endpoint answered without a body.");

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

    /// <summary>The raw value the login/rotation response put in the second cookie.</summary>
    private static string RefreshCookieFrom(HttpResponseMessage response)
    {
        string header = Assert.Single(
            SetCookieHeaders(response),
            cookie => cookie.StartsWith(AuthenticationConfiguration.RefreshCookieName + "=", StringComparison.Ordinal));
        return header.Split(';', 2)[0][(AuthenticationConfiguration.RefreshCookieName.Length + 1)..];
    }

    /// <summary>True when the response expires the second cookie (empty value + epoch expiry).</summary>
    private static bool ClearsRefreshCookie(HttpResponseMessage response)
        => SetCookieHeaders(response).Any(cookie =>
            cookie.Split(';', 2)[0] == AuthenticationConfiguration.RefreshCookieName + "=");

    // ---- Flow helpers --------------------------------------------------------

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

    private static async Task<HttpResponseMessage> LoginAsync(
        HttpClient client, string requestToken, string email)
        => await PostAsJsonAsync(client, $"{ApiBase}/auth/login", new LoginRequestPayload(email, Password), requestToken);

    /// <summary>
    /// Registers and signs in, returning the jar-backed client and the
    /// undisposed login response — its Set-Cookie headers carry the session,
    /// refresh and antiforgery cookies the tests inspect and replay.
    /// </summary>
    private async Task<(HttpClient Client, HttpResponseMessage Login)> RegisterAndLoginAsync(string email)
    {
        (HttpClient client, string registerToken) = await RegisterAsync(email);
        HttpResponseMessage login = await LoginAsync(client, registerToken, email);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return (client, login);
    }

    /// <summary>POST /auth/refresh on a jar-backed client with a fresh antiforgery token.</summary>
    private static async Task<HttpResponseMessage> RefreshAsync(HttpClient client, string requestToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/auth/refresh");
        return await AntiforgeryTestSupport.SendUnsafeAsync(client, request, requestToken);
    }

    /// <summary>
    /// Mints an anonymous antiforgery PAIR on a jar-less client (cookie value
    /// from Set-Cookie, token from the body), so a saved refresh cookie can be
    /// replayed by hand — the jar would otherwise have rotated it away.
    /// </summary>
    private static async Task<(string AntiforgeryCookie, string RequestToken)> MintPairAsync(HttpClient client)
    {
        using var mint = await client.GetAsync($"{ApiBase}/auth/antiforgery");
        Assert.Equal(HttpStatusCode.OK, mint.StatusCode);

        string header = Assert.Single(
            SetCookieHeaders(mint),
            cookie => cookie.StartsWith(AntiforgeryConfiguration.CookieName + "=", StringComparison.Ordinal));
        string cookieValue = header.Split(';', 2)[0][(AntiforgeryConfiguration.CookieName.Length + 1)..];

        var body = await mint.Content.ReadFromJsonAsync<AntiforgeryPayload>();
        Assert.False(string.IsNullOrWhiteSpace(body!.RequestToken), "the antiforgery mint must return a request token");
        return (cookieValue, body.RequestToken!);
    }

    /// <summary>Replays a saved refresh cookie with a hand-built Cookie header (no jar).</summary>
    private static async Task<HttpResponseMessage> RefreshReplayAsync(
        HttpClient client, string antiforgeryCookie, string requestToken, string? refreshToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/auth/refresh");
        string cookieHeader = string.IsNullOrEmpty(refreshToken)
            ? $"{AntiforgeryConfiguration.CookieName}={antiforgeryCookie}"
            : $"{AntiforgeryConfiguration.CookieName}={antiforgeryCookie}; {AuthenticationConfiguration.RefreshCookieName}={refreshToken}";
        request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        request.Headers.TryAddWithoutValidation(AntiforgeryConfiguration.HeaderName, requestToken);
        return await client.SendAsync(request);
    }

    // ---- DB helpers ----------------------------------------------------------

    private async Task<User> ReadUserRowAsync(string email)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StockpileDbContext>();
        // EmailNormalized follows the Identity default normalizer (UpperInvariant);
        // the predicate must translate to SQL — the pragma is the codebase
        // convention for EF-translatable UPPER comparisons (see StockpileRoleStore).
#pragma warning disable CA1862 // `ToUpperInvariant()` translates to SQL UPPER(); StringComparison overloads do not
        return await db.Users.AsNoTracking().FirstAsync(
            row => row.EmailNormalized == email.ToUpperInvariant());
#pragma warning restore CA1862
    }

    /// <summary>The user's most recent login session and every refresh row chained to it.</summary>
    private async Task<(LoginSession Session, List<RefreshSession> Rows)> ReadChainAsync(Guid userId)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StockpileDbContext>();
        LoginSession session = await db.LoginSessions.AsNoTracking()
            .Where(row => row.UserId == userId)
            .OrderByDescending(row => row.IssuedAt)
            .FirstAsync();
        List<RefreshSession> rows = await db.RefreshSessions.AsNoTracking()
            .Where(row => row.SessionId == session.Id)
            .ToListAsync();
        return (session, rows);
    }

    private async Task<SecurityEvent?> LatestSecurityEventForAsync(Guid userId, SecurityEventType type)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StockpileDbContext>();
        return await db.SecurityEvents.AsNoTracking()
            .Where(row => row.UserId == userId && row.Type == type)
            .OrderByDescending(row => row.Id)
            .FirstOrDefaultAsync();
    }

    private async Task<AuditLog?> LatestAuditRowForAsync(string action, string targetId)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StockpileDbContext>();
        return await db.AuditLogs.AsNoTracking()
            .Where(row => row.Action == action && row.TargetId == targetId)
            .OrderByDescending(row => row.Id)
            .FirstOrDefaultAsync();
    }

    // ---- Rotation ------------------------------------------------------------

    [Fact]
    public async Task Login_sets_the_refresh_cookie_and_rotation_replaces_it_on_the_chain()
    {
        // Happy path (architecture §8.1): login carries the second HttpOnly
        // cookie with the documented flags, refresh rotates it (new value ≠
        // old), the session stays alive, and the ledger shows the chain
        // ADVANCED — RotatedToId links old → successor, nothing revoked,
        // nothing deleted (append-only, X9). Rotation is normal operation:
        // no security event.
        string email = NewEmail();
        (HttpClient client, HttpResponseMessage loginResponse) = await RegisterAndLoginAsync(email);
        using HttpResponseMessage login = loginResponse;

        string header = Assert.Single(
            SetCookieHeaders(login),
            cookie => cookie.StartsWith(AuthenticationConfiguration.RefreshCookieName + "=", StringComparison.Ordinal));
        Assert.Contains("HttpOnly", header, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Secure", header, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SameSite=Lax", header, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", header, StringComparison.OrdinalIgnoreCase);

        AuthPayload loginBody = await ReadAuthPayloadAsync(login);
        string first = RefreshCookieFrom(login);
        Assert.False(string.IsNullOrWhiteSpace(first), "login must issue the refresh cookie (task 1.40)");

        using var refreshed = await RefreshAsync(client, loginBody.RequestToken!);
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);

        AuthPayload refreshBody = await ReadAuthPayloadAsync(refreshed);
        Assert.Equal(email, refreshBody.User!.Email);
        Assert.False(string.IsNullOrWhiteSpace(refreshBody.RequestToken), "refresh must re-mint antiforgery (§1.10)");
        string second = RefreshCookieFrom(refreshed);
        Assert.NotEqual(first, second);

        using (var session = await client.GetAsync($"{ApiBase}/auth/session"))
        {
            Assert.Equal(HttpStatusCode.OK, session.StatusCode);
        }

        User user = await ReadUserRowAsync(email);
        (LoginSession sessionRow, List<RefreshSession> rows) = await ReadChainAsync(user.PublicId);
        Assert.Equal(2, rows.Count);
        RefreshSession rotated = Assert.Single(rows, row => row.RotatedToId is not null);
        RefreshSession head = Assert.Single(rows, row => row.RotatedToId is null);
        Assert.Equal(head.Id, rotated.RotatedToId);
        Assert.Equal(rotated.SessionId, head.SessionId);
        Assert.NotEqual(rotated.TokenHash, head.TokenHash);
        Assert.Null(rotated.RevokedAt);
        Assert.Null(head.RevokedAt);
        Assert.Null(sessionRow.RevokedAt);

        Assert.NotNull(await LatestAuditRowForAsync("auth.refresh", sessionRow.Id.ToString()));
        Assert.Null(await LatestSecurityEventForAsync(user.PublicId, SecurityEventType.RefreshReuseDetected));
    }

    [Fact]
    public async Task Replaying_a_rotated_token_revokes_the_whole_chain_and_raises_the_alarm()
    {
        // A9 — reuse of a rotated head is theft evidence: the replay answers
        // one identical 401, EVERY refresh row of the session plus the session
        // itself are revoked (the whole chain dies because no holder can be
        // told apart), RefreshReuseDetected raises the alarm, and the victim's
        // still-open session dies with it.
        string email = NewEmail();
        (HttpClient client, HttpResponseMessage login) = await RegisterAndLoginAsync(email);
        using (login)
        {
            AuthPayload loginBody = await ReadAuthPayloadAsync(login);
            string original = RefreshCookieFrom(login);

            using var rotatedResponse = await RefreshAsync(client, loginBody.RequestToken!);
            Assert.Equal(HttpStatusCode.OK, rotatedResponse.StatusCode);
            string successor = RefreshCookieFrom(rotatedResponse);
            Assert.NotEqual(original, successor);

            using var inspect = AntiforgeryTestSupport.CookieInspectClient(_factory);
            (string antiforgeryCookie, string antiforgeryToken) = await MintPairAsync(inspect);
            using var replay = await RefreshReplayAsync(inspect, antiforgeryCookie, antiforgeryToken, original);

            Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
            var envelope = await replay.Content.ReadFromJsonAsync<ErrorEnvelope>();
            Assert.Equal(ErrorCodes.SessionExpired, envelope!.Code);

            // Repeat replays of the dead cookie stay SILENT — the first
            // detection fired the alarm; a stuck or grinding client must not
            // flood the trail with a row per attempt (rate-limited anyway).
            using var replayAgain = await RefreshReplayAsync(inspect, antiforgeryCookie, antiforgeryToken, original);
            Assert.Equal(HttpStatusCode.Unauthorized, replayAgain.StatusCode);
        }

        User user = await ReadUserRowAsync(email);
        (LoginSession sessionRow, List<RefreshSession> rows) = await ReadChainAsync(user.PublicId);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.NotNull(row.RevokedAt));
        Assert.NotNull(sessionRow.RevokedAt);

        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StockpileDbContext>();
            int alarms = await db.SecurityEvents.AsNoTracking().CountAsync(
                row => row.UserId == user.PublicId && row.Type == SecurityEventType.RefreshReuseDetected);
            Assert.Equal(1, alarms);
        }

        SecurityEvent? reused = await LatestSecurityEventForAsync(user.PublicId, SecurityEventType.RefreshReuseDetected);
        Assert.NotNull(reused);
        Assert.Contains("reuse", reused!.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(await LatestAuditRowForAsync("auth.refresh.reuse", sessionRow.Id.ToString()));

        // The chain death is total: the authenticated session cookie the victim
        // still holds answers 401 too (A9 "whole session chain revoked").
        using var session = await client.GetAsync($"{ApiBase}/auth/session");
        Assert.Equal(HttpStatusCode.Unauthorized, session.StatusCode);
    }

    // ---- Revoked / expired / unknown states ----------------------------------

    [Fact]
    public async Task Refresh_after_logout_all_answers_401_with_the_chain_already_revoked()
    {
        // A8 — logout-all stamps every chain and clears THIS device's cookie;
        // replaying the saved token afterwards answers the same 401, and the
        // ledger already shows the revocation. Logout is not theft: no
        // RefreshReuseDetected alarm fires.
        string email = NewEmail();
        (HttpClient client, HttpResponseMessage login) = await RegisterAndLoginAsync(email);
        using (login)
        {
            AuthPayload loginBody = await ReadAuthPayloadAsync(login);
            string original = RefreshCookieFrom(login);

            using var logout = await PostAsJsonAsync(client, $"{ApiBase}/auth/logout-all", new { }, loginBody.RequestToken!);
            Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
            Assert.True(ClearsRefreshCookie(logout), "logout-all must expire the second cookie on this device too");

            using var inspect = AntiforgeryTestSupport.CookieInspectClient(_factory);
            (string antiforgeryCookie, string antiforgeryToken) = await MintPairAsync(inspect);
            using var replay = await RefreshReplayAsync(inspect, antiforgeryCookie, antiforgeryToken, original);

            Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
            var envelope = await replay.Content.ReadFromJsonAsync<ErrorEnvelope>();
            Assert.Equal(ErrorCodes.SessionExpired, envelope!.Code);
        }

        User user = await ReadUserRowAsync(email);
        (LoginSession sessionRow, List<RefreshSession> rows) = await ReadChainAsync(user.PublicId);
        Assert.NotEmpty(rows);
        Assert.All(rows, row => Assert.NotNull(row.RevokedAt));
        Assert.NotNull(sessionRow.RevokedAt);
        Assert.Null(await LatestSecurityEventForAsync(user.PublicId, SecurityEventType.RefreshReuseDetected));
    }

    [Fact]
    public async Task An_expired_refresh_token_answers_401_and_stamps_the_row_quietly()
    {
        // TTL enforcement over the REAL persisted row (Testing runs
        // Auth:RefreshTokenMinutes=0.17 ≈ 10.2 s): past the window the same
        // 401, the row is stamped as dead — and quiet, because expiry is not
        // theft: the session itself stays alive and no alarm fires.
        string email = NewEmail();
        (HttpClient client, HttpResponseMessage login) = await RegisterAndLoginAsync(email);
        string original;
        using (login)
        {
            original = RefreshCookieFrom(login);
        }

        await Task.Delay(PastRefreshTtl);

        using var inspect = AntiforgeryTestSupport.CookieInspectClient(_factory);
        (string antiforgeryCookie, string antiforgeryToken) = await MintPairAsync(inspect);
        using var expired = await RefreshReplayAsync(inspect, antiforgeryCookie, antiforgeryToken, original);

        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
        var envelope = await expired.Content.ReadFromJsonAsync<ErrorEnvelope>();
        Assert.Equal(ErrorCodes.SessionExpired, envelope!.Code);

        User user = await ReadUserRowAsync(email);
        (LoginSession sessionRow, List<RefreshSession> rows) = await ReadChainAsync(user.PublicId);
        Assert.NotEmpty(rows);
        Assert.All(rows, row => Assert.NotNull(row.RevokedAt));
        Assert.Null(sessionRow.RevokedAt);
        Assert.Null(await LatestSecurityEventForAsync(user.PublicId, SecurityEventType.RefreshReuseDetected));
    }

    [Fact]
    public async Task An_unknown_or_absent_refresh_cookie_is_the_same_401()
    {
        // One envelope for every dead state — unknown, absent, expired,
        // revoked, rotated (the reason is never revealed). The dead value
        // self-expires at the cookie's own Expires attribute, and the next
        // sign-in overwrites it, so no failure-path reset is needed — the
        // exception path owns the response (headers included).
        using var inspect = AntiforgeryTestSupport.CookieInspectClient(_factory);
        (string antiforgeryCookie, string antiforgeryToken) = await MintPairAsync(inspect);

        string unknown = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        using var rejected = await RefreshReplayAsync(inspect, antiforgeryCookie, antiforgeryToken, unknown);
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        var envelope = await rejected.Content.ReadFromJsonAsync<ErrorEnvelope>();
        Assert.Equal(ErrorCodes.SessionExpired, envelope!.Code);

        using var absent = await RefreshReplayAsync(inspect, antiforgeryCookie, antiforgeryToken, refreshToken: null);
        Assert.Equal(HttpStatusCode.Unauthorized, absent.StatusCode);
        envelope = await absent.Content.ReadFromJsonAsync<ErrorEnvelope>();
        Assert.Equal(ErrorCodes.SessionExpired, envelope!.Code);
    }

    [Fact]
    public async Task Logout_expires_the_refresh_cookie_and_the_saved_token_stops_working()
    {
        // Single-device logout clears the second cookie on the wire and stamps
        // its chain server-side — the value captured before logout is dead the
        // moment the session is.
        string email = NewEmail();
        (HttpClient client, HttpResponseMessage login) = await RegisterAndLoginAsync(email);
        string original;
        using (login)
        {
            AuthPayload loginBody = await ReadAuthPayloadAsync(login);
            original = RefreshCookieFrom(login);

            using var logout = await PostAsJsonAsync(client, $"{ApiBase}/auth/logout", new { }, loginBody.RequestToken!);
            Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
            Assert.True(ClearsRefreshCookie(logout), "logout must expire the second cookie");
        }

        using var inspect = AntiforgeryTestSupport.CookieInspectClient(_factory);
        (string antiforgeryCookie, string antiforgeryToken) = await MintPairAsync(inspect);
        using var replay = await RefreshReplayAsync(inspect, antiforgeryCookie, antiforgeryToken, original);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        var envelope = await replay.Content.ReadFromJsonAsync<ErrorEnvelope>();
        Assert.Equal(ErrorCodes.SessionExpired, envelope!.Code);

        User user = await ReadUserRowAsync(email);
        (_, List<RefreshSession> rows) = await ReadChainAsync(user.PublicId);
        Assert.NotEmpty(rows);
        Assert.All(rows, row => Assert.NotNull(row.RevokedAt));
    }

    // ---- Wire payloads -------------------------------------------------------

    private sealed record RegisterRequestPayload(
        string FirstName, string LastName, string Email, string? Phone, string Password);

    private sealed record LoginRequestPayload(string Email, string Password);

    private sealed record RegisterResponsePayload(string? Status, string? RequestToken);

    /// <summary>Login and refresh answer the same body (api.md §2.1).</summary>
    private sealed record AuthPayload(string? RequestToken, AuthSessionResponse? User);

    private sealed record AntiforgeryPayload(string? RequestToken);
}
