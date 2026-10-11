using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Stockpile.Contracts.Authentication;
using Stockpile.Contracts.Errors;
using Stockpile.Domain.Entities.Identity;
using Stockpile.Infrastructure.Persistence;
using Stockpile.IntegrationTests.Testing;

namespace Stockpile.IntegrationTests.Authorization;

/// <summary>
/// Task 1.41/1.42 — the five-layer pipeline's function-level gates over real
/// HTTP (security-test-matrix F1, F2, F11, and the runtime twin of F10;
/// threat-model T6.1, architecture §8.2). The unlock endpoint is the shipped
/// permission-gated surface: its policy resolves the caller's grants from the
/// seeded role bindings SERVER-SIDE on every request — never from the issued
/// ticket — so the deny/allow/revoke sequence below proves capability changes
/// land on the very next call. F11 rides the same denial: the 403 body is the
/// §1.4 envelope and nothing else, carrying no entity data.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class FunctionAuthorizationApiTests
{
    private readonly StockpileApiFactory _factory;

    /// <summary>Shares the one API host (single migrate + seed of Stockpile_Test).</summary>
    public FunctionAuthorizationApiTests(StockpileApiFactory factory) => _factory = factory;

    private const string Password = "correct-horse-battery-staple";
    private const string ApiBase = "/api/v1";

    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    private static string NewEmail() => $"authz.{Guid.NewGuid():N}@test.local";

    private static RegisterRequestPayload RegisterPayload(string email) => new(
        FirstName: "Test",
        LastName: "Caller",
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

    private static async Task<LoginPayload> ReadLoginAsync(HttpResponseMessage response)
        => await response.Content.ReadFromJsonAsync<LoginPayload>()
            ?? throw new InvalidOperationException("Login answered without a body.");

    // ---- Flow helpers --------------------------------------------------------

    /// <summary>Registers an account and returns the client plus the register-minted request token.</summary>
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

    /// <summary>Registers, signs in, and returns the client plus the login-minted request token.</summary>
    private async Task<(HttpClient Client, string RequestToken)> LoginAsync(string email)
    {
        (HttpClient client, string registerToken) = await RegisterAsync(email);
        using var login = await LoginAsync(client, registerToken, email);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        LoginPayload body = await ReadLoginAsync(login);
        Assert.False(string.IsNullOrWhiteSpace(body.RequestToken), "login must re-mint a request token (§1.10)");
        return (client, body.RequestToken!);
    }

    /// <summary>Grants a seeded role over the real UserManager (Seed:Roles rows exist in the Testing host).</summary>
    private async Task GrantRoleAsync(string email, string role)
        => await SetRoleAsync(email, role, addToRole: true);

    /// <summary>Revokes a seeded role over the real UserManager — the deny side of the per-request resolution.</summary>
    private async Task RevokeRoleAsync(string email, string role)
        => await SetRoleAsync(email, role, addToRole: false);

    private async Task SetRoleAsync(string email, string role, bool addToRole)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        User user = await users.FindByEmailAsync(email)
            ?? throw new InvalidOperationException($"No user row for {email}.");
        IdentityResult result = addToRole
            ? await users.AddToRoleAsync(user, role)
            : await users.RemoveFromRoleAsync(user, role);
        Assert.True(
            result.Succeeded,
            $"setting '{role}' failed: {string.Join("; ", result.Errors.Select(error => error.Description))}");
    }

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

    // ---- F1 + F11: buyer denied, envelope carries no entity data -------------

    [Fact]
    public async Task A_buyer_is_denied_the_admin_surface_with_a_bare_envelope()
    {
        // F1 — a plain registered buyer (default "buyer" role, no admin grants)
        // never reaches the action: authorization runs before the endpoint, so
        // even a random target id answers 403, not 404. F11 rides the same
        // response — code + message envelope members only, no field errors and
        // nothing about the target account.
        string email = NewEmail();
        (HttpClient client, string requestToken) = await LoginAsync(email);

        using var unlock = await PostAsJsonAsync(
            client, $"{ApiBase}/admin/users/{Guid.NewGuid()}/unlock", new { }, requestToken);
        Assert.Equal(HttpStatusCode.Forbidden, unlock.StatusCode);

        ErrorEnvelope envelope = await unlock.Content.ReadFromJsonAsync<ErrorEnvelope>()
            ?? throw new InvalidOperationException("403 answered without an envelope.");
        Assert.Equal(ErrorCodes.InsufficientCapability, envelope.Code);

        using JsonDocument document = JsonDocument.Parse(await unlock.Content.ReadAsStringAsync());
        HashSet<string> keys = document.RootElement.EnumerateObject()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
        string[] envelopeKeys = ["code", "message", "correlationId", "supportReference", "retryable", "timestamp"];
        Assert.Equal(envelopeKeys.Length, keys.Count);
        foreach (string key in envelopeKeys)
        {
            Assert.Contains(key, keys);
        }

        Assert.DoesNotContain("fieldErrors", keys); // no validation residue, no entity payload
    }

    // ---- F2: supplier denied -------------------------------------------------

    [Fact]
    public async Task A_supplier_is_denied_the_admin_surface()
    {
        // F2 — a seeded supplier user holds the supplier grant set, never
        // admin.users.unlock: function-level authorization refuses the whole
        // /admin/** surface by capability, not by guessing role names.
        string email = NewEmail();
        (HttpClient client, string registerToken) = await RegisterAsync(email);
        await GrantRoleAsync(email, "supplierUser");
        using var login = await LoginAsync(client, registerToken, email);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        LoginPayload body = await ReadLoginAsync(login);

        using var unlock = await PostAsJsonAsync(
            client, $"{ApiBase}/admin/users/{Guid.NewGuid()}/unlock", new { }, body.RequestToken!);
        Assert.Equal(HttpStatusCode.Forbidden, unlock.StatusCode);
        ErrorEnvelope envelope = await unlock.Content.ReadFromJsonAsync<ErrorEnvelope>()
            ?? throw new InvalidOperationException("403 answered without an envelope.");
        Assert.Equal(ErrorCodes.InsufficientCapability, envelope.Code);
    }

    // ---- Layer 2: server-side resolution, per request ------------------------

    [Fact]
    public async Task Permission_grants_are_resolved_on_every_request_not_from_the_ticket()
    {
        // The pipeline's core property (architecture §8.2 layer 2,
        // IUserPermissionStore): the capability set is loaded from the seeded
        // role bindings on EACH request. One session, one antiforgery token —
        // only the grant changes: deny (buyer grants) → allow (role granted
        // after sign-in) → deny (role revoked): every step lands on the very
        // next call, proving no issued claim ever carries capability.
        string email = NewEmail();
        (HttpClient client, string requestToken) = await LoginAsync(email);
        User self = await ReadUserRowAsync(email);
        string unlockRoute = $"{ApiBase}/admin/users/{self.PublicId}/unlock"; // idempotent: 204 even when not locked

        using (var before = await PostAsJsonAsync(client, unlockRoute, new { }, requestToken))
        {
            Assert.Equal(HttpStatusCode.Forbidden, before.StatusCode);
        }

        await GrantRoleAsync(email, "platformAdministrator");
        using (var granted = await PostAsJsonAsync(client, unlockRoute, new { }, requestToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, granted.StatusCode);
        }

        await RevokeRoleAsync(email, "platformAdministrator");
        using (var revoked = await PostAsJsonAsync(client, unlockRoute, new { }, requestToken))
        {
            Assert.Equal(HttpStatusCode.Forbidden, revoked.StatusCode);
            ErrorEnvelope envelope = await revoked.Content.ReadFromJsonAsync<ErrorEnvelope>()
                ?? throw new InvalidOperationException("403 answered without an envelope.");
            Assert.Equal(ErrorCodes.InsufficientCapability, envelope.Code);
        }
    }

    // ---- F10 runtime twin: unknown policy names fail closed ------------------

    [Fact]
    public async Task An_unknown_policy_name_denies_anonymous_and_signed_in_callers_alike()
    {
        // The PermissionPolicyProvider resolves any name that is neither a
        // registered policy nor a Contracts permission code to an unsatisfiable
        // policy (threat-model T6.1 "default is deny"): a typo'd policy must
        // never authorize anyone — 401 anonymous, 403 for a live ticket — and
        // the action body stays unreachable either way (F10's runtime twin;
        // the build-time gate is AuthorizationDeclarationsTests).
        HttpClient anonymous = AntiforgeryTestSupport.HttpsClient(_factory);
        using var denied = await anonymous.GetAsync("/_test/unknown-policy");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        ErrorEnvelope anonymousEnvelope = await denied.Content.ReadFromJsonAsync<ErrorEnvelope>()
            ?? throw new InvalidOperationException("401 answered without an envelope.");
        Assert.Equal(ErrorCodes.Unauthenticated, anonymousEnvelope.Code);

        HttpClient signedIn = AntiforgeryTestSupport.HttpsClient(_factory);
        using var signIn = await signedIn.GetAsync("/_test/signin");
        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
        using var forbidden = await signedIn.GetAsync("/_test/unknown-policy");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        ErrorEnvelope signedInEnvelope = await forbidden.Content.ReadFromJsonAsync<ErrorEnvelope>()
            ?? throw new InvalidOperationException("403 answered without an envelope.");
        Assert.Equal(ErrorCodes.InsufficientCapability, signedInEnvelope.Code);
    }

    // ---- The fallback floor must not hijack routing's own refusals -----------

    [Fact]
    public async Task Unmatched_routes_keep_routings_404_and_405_under_the_deny_by_default_floor()
    {
        // The framework applies the fallback policy to ANY request without
        // authorization metadata — including ones no endpoint matched, which
        // carry no AllowAnonymous metadata for the middleware's short-circuit
        // to find. DenyByDefaultRequirement passes those through: routing
        // refused them before endpoint code could run, so they must answer
        // their own 404/405 — never a bogus 401 challenge that would mask
        // route existence behind an auth error.
        HttpClient anonymous = AntiforgeryTestSupport.HttpsClient(_factory);

        using var missing = await anonymous.GetAsync($"{ApiBase}/no-such-route-{Guid.NewGuid():N}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        // logout is POST-only: the method mismatch's 405 (the _test-seam twin
        // of this case is AntiforgeryApiTests.C5_get_on_a_write_route_is_405).
        using var mismatch = await anonymous.GetAsync($"{ApiBase}/auth/logout");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, mismatch.StatusCode);
    }

    // ---- Wire shapes -----------------------------------------------------------

    private sealed record RegisterRequestPayload(
        string FirstName, string LastName, string Email, string? Phone, string Password);

    private sealed record LoginRequestPayload(string Email, string Password);

    private sealed record RegisterResponsePayload(string? Status, string? RequestToken);

    private sealed record LoginPayload(string? RequestToken, AuthSessionResponse? User);
}
