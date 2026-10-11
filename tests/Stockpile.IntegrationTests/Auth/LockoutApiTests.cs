using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Stockpile.Contracts.Authentication;
using Stockpile.Contracts.Errors;
using Stockpile.Domain.Entities.Identity;
using Stockpile.Domain.Entities.Platform;
using Stockpile.Domain.Enums;
using Stockpile.Infrastructure.Persistence;
using Stockpile.IntegrationTests.Testing;

namespace Stockpile.IntegrationTests.Auth;

/// <summary>
/// Task 1.39 — the exponential lockout backoff with jitter and the admin
/// unlock, over real HTTP (security-test-matrix A1/F7, threat-model T1.1 and
/// §13.4 line 168). The Testing host runs <c>Auth:LockoutBaseMinutes=0.17</c>,
/// so episode 1 lasts 0.17×[0.8,1.2] ≈ 8.2–12.2 s and episode 2 doubles that —
/// the suite waits past episode 1's WORST case and then reads the persisted
/// <c>Users</c> row, asserting the jittered band directly rather than trusting
/// a rounded message.
/// <para>
/// The task 1.41/1.42 permission pipeline gates the unlock endpoint now: the
/// <c>admin.users.unlock</c> policy resolves the caller's grants server-side
/// on every request (F7's <c>financeOperator</c> proves the negative). Role
/// grants happen over DI (the real <c>UserManager.AddToRoleAsync</c> against
/// the seeded <c>Seed:Roles</c> rows), and every login/lockout assertion runs
/// through the production use case, never a seam.
/// </para>
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class LockoutApiTests
{
    private readonly StockpileApiFactory _factory;

    /// <summary>Shares the one API host (single migrate + seed of Stockpile_Test).</summary>
    public LockoutApiTests(StockpileApiFactory factory) => _factory = factory;

    private const string Password = "correct-horse-battery-staple";
    private const string ApiBase = "/api/v1";

    /// <summary>
    /// Episode 1's longest possible window (12.24 s) plus margin — sleeping
    /// this long guarantees the first lockout expired, whatever jitter drew.
    /// </summary>
    private static readonly TimeSpan PastEpisodeOne = TimeSpan.FromSeconds(13.5);

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

    private static async Task<LoginPayload> ReadLoginAsync(HttpResponseMessage response)
        => await response.Content.ReadFromJsonAsync<LoginPayload>()
            ?? throw new InvalidOperationException("Login answered without a body.");

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

    /// <summary>Fires <paramref name="count"/> wrong-password sign-ins and asserts the generic 401.</summary>
    private static async Task FailLoginsAsync(HttpClient client, string requestToken, string email, int count)
    {
        for (int attempt = 0; attempt < count; attempt++)
        {
            using var rejected = await PostAsJsonAsync(
                client, $"{ApiBase}/auth/login",
                new LoginRequestPayload(email, $"wrong-{attempt}-{Guid.NewGuid():N}"), requestToken);
            Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
            var envelope = await rejected.Content.ReadFromJsonAsync<ErrorEnvelope>();
            Assert.Equal(ErrorCodes.InvalidCredentials, envelope!.Code);
        }
    }

    /// <summary>Grants a seeded role over the real UserManager (Seed:Roles rows exist in the Testing host).</summary>
    private async Task GrantRoleAsync(string email, string role)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        User user = await users.FindByEmailAsync(email)
            ?? throw new InvalidOperationException($"No user row for {email}.");
        IdentityResult result = await users.AddToRoleAsync(user, role);
        Assert.True(
            result.Succeeded,
            $"granting '{role}' failed: {string.Join("; ", result.Errors.Select(error => error.Description))}");
    }

    /// <summary>
    /// Registers an account, grants it a seeded role (post-registration — the
    /// row must exist before the role grant can attach a <c>UserRole</c>), then
    /// signs in and returns the authenticated client plus its fresh request token.
    /// </summary>
    private async Task<(HttpClient Client, string LoginToken)> LoginAsRoleUserAsync(string email, string role)
    {
        var (client, registerToken) = await RegisterAsync(email);
        await GrantRoleAsync(email, role);
        using var login = await LoginAsync(client, registerToken, email);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        LoginPayload body = await ReadLoginAsync(login);
        Assert.False(string.IsNullOrWhiteSpace(body.RequestToken), "login must re-mint a request token (§1.10)");
        return (client, body.RequestToken!);
    }

    // ---- DB helpers ------------------------------------------------------------

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

    // ---- A1: the backoff ladder ---------------------------------------------

    [Fact]
    public async Task A_fifth_failure_locks_the_account_with_a_jittered_episode_one_window()
    {
        // A1 + task 1.39 — the fifth failure triggers the FIRST backoff rung:
        // the persisted end sits at base × [0.8, 1.2] (Testing base 0.17 min ⇒
        // ≈ 8.2–12.2 s), the security event names the episode, and the next
        // attempt answers 403 ACCOUNT_LOCKED with an honest "about 1 minute".
        string email = NewEmail();
        (HttpClient client, string registerToken) = await RegisterAsync(email);
        await FailLoginsAsync(client, registerToken, email, 5);

        using var locked = await LoginAsync(client, registerToken, email);
        Assert.Equal(HttpStatusCode.Forbidden, locked.StatusCode);
        var envelope = await locked.Content.ReadFromJsonAsync<ErrorEnvelope>();
        Assert.Equal(ErrorCodes.AccountLocked, envelope!.Code);
        Assert.Contains("locked", envelope.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("about 1 minute", envelope.Message, StringComparison.Ordinal);

        User row = await ReadUserRowAsync(email);
        Assert.Equal(1, row.LockoutEpisode);
        // .NET 10 resets the counter the moment the lockout triggers — each
        // episode earns a fresh threshold (verified against the real UserManager).
        Assert.Equal(0, row.FailedAccessCount);
        Assert.NotNull(row.LockoutEndsAt);
        TimeSpan remaining = row.LockoutEndsAt!.Value - DateTime.UtcNow;
        Assert.True(remaining > TimeSpan.FromSeconds(6), $"episode 1 window too short: {remaining}");
        Assert.True(remaining < TimeSpan.FromSeconds(14), $"episode 1 window too long: {remaining}");

        // The LockoutTriggered event names the episode — repeated lockouts are
        // observable as an escalating series, not a silent permanent lock.
        SecurityEvent? triggered = await LatestSecurityEventForAsync(row.PublicId, SecurityEventType.LockoutTriggered);
        Assert.NotNull(triggered);
        Assert.Contains("episode 1", triggered!.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_backoff_window_doubles_for_a_second_lockout_episode()
    {
        // The ladder's whole point: after episode 1 expires, ONE more failure
        // re-locks with episode 2 ≈ 2 × base × [0.8, 1.2] ≈ 16.3–24.5 s. The
        // trigger is detected by the threshold (the counter survives the window),
        // so growth does not depend on Identity re-flagging the end.
        string email = NewEmail();
        (HttpClient client, string registerToken) = await RegisterAsync(email);
        await FailLoginsAsync(client, registerToken, email, 5);

        await Task.Delay(PastEpisodeOne); // past episode 1's WORST case (12.24 s)

        // The counter reset to zero when episode 1 triggered, so re-locking
        // needs a FRESH threshold — five more wrong passwords, the fifth of
        // which crosses and opens episode 2 (≈ 2 × base × [0.8, 1.2]).
        await FailLoginsAsync(client, registerToken, email, 5);

        User row = await ReadUserRowAsync(email);
        Assert.Equal(2, row.LockoutEpisode);
        Assert.NotNull(row.LockoutEndsAt);
        TimeSpan remaining = row.LockoutEndsAt!.Value - DateTime.UtcNow;
        Assert.True(remaining > TimeSpan.FromSeconds(14), $"episode 2 must be ~2× episode 1, got {remaining}");
        Assert.True(remaining < TimeSpan.FromSeconds(27), $"episode 2 window too long: {remaining}");

        // …and it is immediately enforced: the correct password still 403s.
        using var stillLocked = await LoginAsync(client, registerToken, email);
        Assert.Equal(HttpStatusCode.Forbidden, stillLocked.StatusCode);
        var envelope = await stillLocked.Content.ReadFromJsonAsync<ErrorEnvelope>();
        Assert.Equal(ErrorCodes.AccountLocked, envelope!.Code);

        SecurityEvent? triggered = await LatestSecurityEventForAsync(row.PublicId, SecurityEventType.LockoutTriggered);
        Assert.NotNull(triggered);
        Assert.Contains("episode 2", triggered!.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_successful_sign_in_after_the_window_resets_the_backoff_ladder()
    {
        // The victim's OWN success re-arms the ladder: episode and counter drop
        // to zero, so their next incident starts at the base window again —
        // while an attacker's continued failures (which never sign in) cannot
        // reset anything.
        string email = NewEmail();
        (HttpClient client, string registerToken) = await RegisterAsync(email);
        await FailLoginsAsync(client, registerToken, email, 5);

        await Task.Delay(PastEpisodeOne);

        using var login = await LoginAsync(client, registerToken, email);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        User row = await ReadUserRowAsync(email);
        Assert.Equal(0, row.LockoutEpisode);
        Assert.Equal(0, row.FailedAccessCount);
        Assert.Null(row.LockoutEndsAt);
    }

    // ---- F7: the admin unlock ------------------------------------------------

    [Fact]
    public async Task The_platform_admin_unlocks_a_locked_account_over_http()
    {
        // The bounded admin surface (threat-model §13.4 line 168 "admin unlock"):
        // a real platformAdministrator — seeded role granted over DI, real login,
        // real antiforgery — clears a locked account; the victim then signs in
        // and the row (episode, counter, end) is fully reset. The unlock also
        // writes its LockoutReleased security event and §13.16 audit row.
        string victimEmail = NewEmail();
        (HttpClient victimClient, string victimToken) = await RegisterAsync(victimEmail);
        await FailLoginsAsync(victimClient, victimToken, victimEmail, 5);
        using (var locked = await LoginAsync(victimClient, victimToken, victimEmail))
        {
            Assert.Equal(HttpStatusCode.Forbidden, locked.StatusCode);
        }

        string adminEmail = NewEmail();
        (HttpClient adminClient, string adminLoginToken) = await LoginAsRoleUserAsync(adminEmail, "platformAdministrator");

        User victim = await ReadUserRowAsync(victimEmail);
        using var unlock = await PostAsJsonAsync(
            adminClient, $"{ApiBase}/admin/users/{victim.PublicId}/unlock", new { }, adminLoginToken);
        Assert.Equal(HttpStatusCode.NoContent, unlock.StatusCode);

        // The account opens immediately — no waiting out the window.
        using var recovered = await LoginAsync(victimClient, victimToken, victimEmail);
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);

        User row = await ReadUserRowAsync(victimEmail);
        Assert.Equal(0, row.LockoutEpisode);
        Assert.Equal(0, row.FailedAccessCount);
        Assert.Null(row.LockoutEndsAt);

        SecurityEvent? released = await LatestSecurityEventForAsync(victim.PublicId, SecurityEventType.LockoutReleased);
        Assert.NotNull(released);
        Assert.Contains("administrator", released!.Detail, StringComparison.OrdinalIgnoreCase);

        AuditLog? audit = await LatestAuditRowForAsync("admin.users.unlock", victim.PublicId.ToString());
        Assert.NotNull(audit);
        Assert.Equal(AuditResult.Success, audit!.Result);
    }

    [Fact]
    public async Task A_finance_operator_cannot_unlock_an_account()
    {
        // F7 — function-level authorization (API5): a lesser platform role is
        // refused outright with the envelope's 403 code, and the victim stays
        // locked. The gate is the permission pipeline (1.41/1.42): the
        // financeOperator's seeded grants never include admin.users.unlock.
        string victimEmail = NewEmail();
        (HttpClient victimClient, string victimToken) = await RegisterAsync(victimEmail);
        await FailLoginsAsync(victimClient, victimToken, victimEmail, 5);

        string operatorEmail = NewEmail();
        (HttpClient operatorClient, string operatorLoginToken) = await LoginAsRoleUserAsync(operatorEmail, "financeOperator");

        User victim = await ReadUserRowAsync(victimEmail);
        using var unlock = await PostAsJsonAsync(
            operatorClient, $"{ApiBase}/admin/users/{victim.PublicId}/unlock", new { }, operatorLoginToken);

        Assert.Equal(HttpStatusCode.Forbidden, unlock.StatusCode);
        var envelope = await unlock.Content.ReadFromJsonAsync<ErrorEnvelope>();
        Assert.Equal(ErrorCodes.InsufficientCapability, envelope!.Code);

        // Denied means untouched: the lockout state is exactly as it was
        // (counter reset to zero at the trigger, episode and end untouched).
        User row = await ReadUserRowAsync(victimEmail);
        Assert.Equal(1, row.LockoutEpisode);
        Assert.NotNull(row.LockoutEndsAt);
        Assert.Equal(0, row.FailedAccessCount);
    }

    [Fact]
    public async Task Unlocking_an_unknown_account_is_404_and_an_anonymous_call_is_401()
    {
        // Admin context: an unknown id is an outright 404 (existence checks are
        // meaningless for a platform administrator — no BOLA games), while an
        // anonymous caller never gets past authentication at all.
        string adminEmail = NewEmail();
        (HttpClient adminClient, string adminLoginToken) = await LoginAsRoleUserAsync(adminEmail, "platformAdministrator");

        using var unknown = await PostAsJsonAsync(
            adminClient, $"{ApiBase}/admin/users/{Guid.NewGuid()}/unlock", new { }, adminLoginToken);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        var unknownEnvelope = await unknown.Content.ReadFromJsonAsync<ErrorEnvelope>();
        Assert.Equal(ErrorCodes.ResourceNotFound, unknownEnvelope!.Code);

        using var anonymousClient = AntiforgeryTestSupport.HttpsClient(_factory);
        string anonToken = await AntiforgeryTestSupport.AcquireTokenAsync(anonymousClient);
        using var anonymous = await PostAsJsonAsync(
            anonymousClient, $"{ApiBase}/admin/users/{Guid.NewGuid()}/unlock", new { }, anonToken);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        var anonEnvelope = await anonymous.Content.ReadFromJsonAsync<ErrorEnvelope>();
        Assert.Equal(ErrorCodes.Unauthenticated, anonEnvelope!.Code);
    }

    // ---- Wire shapes -----------------------------------------------------------

    private sealed record RegisterRequestPayload(
        string FirstName, string LastName, string Email, string? Phone, string Password);

    private sealed record LoginRequestPayload(string Email, string Password);

    private sealed record RegisterResponsePayload(string? Status, string? RequestToken);

    private sealed record LoginPayload(string? RequestToken, AuthSessionResponse? User);
}
