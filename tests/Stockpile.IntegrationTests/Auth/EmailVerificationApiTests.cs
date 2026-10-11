using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Stockpile.Contracts.Authentication;
using Stockpile.Contracts.Errors;
using Stockpile.Domain.Entities.Platform;
using Stockpile.Infrastructure.Persistence;
using Stockpile.IntegrationTests.Testing;

namespace Stockpile.IntegrationTests.Auth;

/// <summary>
/// Task 1.38 — email verification and the A11 gate over real HTTP (threat-model
/// line 169, security-test-matrix A11, api.md §2.1): the one-time token from the
/// verification email is consumed by the anonymous <c>POST /auth/email/verify</c>
/// (single-use; one identical 400 <c>INVALID_VERIFICATION_TOKEN</c> for an
/// unknown, used, expired or re-used token — the reason is never revealed); the
/// authenticated resend mints a fresh token and kills the outstanding one; and
/// the verification-gated action answers 403 <c>EMAIL_NOT_VERIFIED</c> until the
/// address is confirmed. The verification email itself is the
/// <c>identity.user.registered</c> outbox row whose payload carries the opaque
/// token for the renderer — the tests read that row (the delivery record), the
/// same channel the future email renderer consumes.
/// <para>
/// Antiforgery follows the §1.10 flow exactly, and the shared API host's Testing
/// TTL (~10 s, <c>Auth__EmailVerificationTokenMinutes=0.17</c>) lets the expiry
/// test drive the real persisted-row expiry.
/// </para>
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class EmailVerificationApiTests
{
    private readonly StockpileApiFactory _factory;

    /// <summary>Shares the one API host (single migrate + seed of Stockpile_Test).</summary>
    public EmailVerificationApiTests(StockpileApiFactory factory) => _factory = factory;

    private const string Password = "correct-horse-battery-staple";
    private const string ApiBase = "/api/v1";

    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    private static string NewEmail() => $"verify.{Guid.NewGuid():N}@test.local";

    // ---- The happy path: register queues the email, the token verifies -------

    [Fact]
    public async Task Register_queues_the_verification_email_and_the_token_verifies()
    {
        string email = NewEmail();
        var (client, requestToken) = await RegisterAsync(_factory, email);

        // The outbox event IS the queued email: identity.user.registered with the
        // opaque token for the future renderer (architecture §10.2).
        string verificationToken = await ReadVerificationEmailTokenAsync(email);

        using var verified = await PostAsJsonAsync(
            client, $"{ApiBase}/auth/email/verify",
            new EmailVerifyRequestPayload(verificationToken), requestToken);
        Assert.Equal(HttpStatusCode.OK, verified.StatusCode);
        var body = await verified.Content.ReadFromJsonAsync<EmailVerificationPayload>();
        Assert.Equal("verified", body!.Status);

        // The account signs in as usual and reports verification state.
        var (loggedIn, _) = await LoginNewClientAsync(_factory, email, Password);
        using (var session = await loggedIn.GetAsync($"{ApiBase}/auth/session"))
        {
            Assert.Equal(HttpStatusCode.OK, session.StatusCode);
            var identity = await session.Content.ReadFromJsonAsync<AuthSessionResponse>();
            Assert.NotNull(identity);
            Assert.True(identity.EmailConfirmed, "the session payload must reflect the verified address");
        }

        loggedIn.Dispose();
        client.Dispose();
    }

    // ---- Single-use, unknown/expired, one identical 400 ----------------------

    [Fact]
    public async Task The_verification_token_is_single_use()
    {
        string email = NewEmail();
        var (client, requestToken) = await RegisterAsync(_factory, email);
        string token = await ReadVerificationEmailTokenAsync(email);

        using var first = await PostAsJsonAsync(
            client, $"{ApiBase}/auth/email/verify", new EmailVerifyRequestPayload(token), requestToken);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using var replay = await PostAsJsonAsync(
            client, $"{ApiBase}/auth/email/verify", new EmailVerifyRequestPayload(token), requestToken);
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        ErrorEnvelope envelope = await ReadEnvelopeAsync(replay);
        Assert.Equal(ErrorCodes.VerificationTokenInvalid, envelope.Code);
    }

    [Fact]
    public async Task An_unknown_token_answers_one_identical_400()
    {
        using var client = AntiforgeryTestSupport.HttpsClient(_factory);
        string requestToken = await AntiforgeryTestSupport.AcquireTokenAsync(client);

        // A 256-bit guess cannot validate against anything — no account lookup
        // even happens, so the endpoint cannot enumerate.
        using var response = await PostAsJsonAsync(
            client, $"{ApiBase}/auth/email/verify",
            new EmailVerifyRequestPayload(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))), requestToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        ErrorEnvelope envelope = await ReadEnvelopeAsync(response);
        Assert.Equal(ErrorCodes.VerificationTokenInvalid, envelope.Code);
    }

    [Fact]
    public async Task An_expired_token_is_rejected_but_resend_recovers()
    {
        // The Testing TTL is ~10 s (Auth__EmailVerificationTokenMinutes=0.17), so
        // the REAL persisted-row expiry drives this path: mint, wait past it, verify.
        string email = NewEmail();
        var (client, requestToken) = await RegisterAsync(_factory, email);
        string firstToken = await ReadVerificationEmailTokenAsync(email);

        await Task.Delay(TimeSpan.FromSeconds(12));

        using var expired = await PostAsJsonAsync(
            client, $"{ApiBase}/auth/email/verify", new EmailVerifyRequestPayload(firstToken), requestToken);
        Assert.Equal(HttpStatusCode.BadRequest, expired.StatusCode);
        ErrorEnvelope envelope = await ReadEnvelopeAsync(expired);
        Assert.Equal(ErrorCodes.VerificationTokenInvalid, envelope.Code);
        Assert.Contains("invalid or has expired", envelope.Message);

        // Recovery is the authenticated resend: a fresh token arrives (the old
        // link was dead anyway) and verifies.
        var (loggedIn, authToken) = await LoginNewClientAsync(_factory, email, Password);
        using (var resent = await PostEmptyAsync(loggedIn, $"{ApiBase}/auth/email/verify/resend", authToken))
        {
            Assert.Equal(HttpStatusCode.Accepted, resent.StatusCode);
            var resentBody = await resent.Content.ReadFromJsonAsync<EmailVerificationPayload>();
            Assert.Equal("verification_resent", resentBody!.Status);
        }

        string newToken = await ReadVerificationEmailTokenAsync(email);
        Assert.NotEqual(firstToken, newToken);
        using (var recovered = await PostAsJsonAsync(
            loggedIn, $"{ApiBase}/auth/email/verify", new EmailVerifyRequestPayload(newToken), authToken))
        {
            Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        }

        loggedIn.Dispose();
        client.Dispose();
    }

    // ---- Resend: one live token per account ----------------------------------

    [Fact]
    public async Task Resend_invalidates_the_outstanding_token_and_mints_a_new_one()
    {
        string email = NewEmail();
        var (client, registerToken) = await RegisterAsync(_factory, email);
        string firstToken = await ReadVerificationEmailTokenAsync(email);
        var (loggedIn, authToken) = await LoginNewClientAsync(_factory, email, Password);

        using (var resent = await PostEmptyAsync(loggedIn, $"{ApiBase}/auth/email/verify/resend", authToken))
        {
            Assert.Equal(HttpStatusCode.Accepted, resent.StatusCode);
        }

        string secondToken = await ReadVerificationEmailTokenAsync(email);
        Assert.NotEqual(firstToken, secondToken);

        // OWASP "invalidate old tokens": the old link dies the moment a new one
        // is issued.
        using (var old = await PostAsJsonAsync(
            loggedIn, $"{ApiBase}/auth/email/verify", new EmailVerifyRequestPayload(firstToken), authToken))
        {
            Assert.Equal(HttpStatusCode.BadRequest, old.StatusCode);
            ErrorEnvelope envelope = await ReadEnvelopeAsync(old);
            Assert.Equal(ErrorCodes.VerificationTokenInvalid, envelope.Code);
        }

        using (var fresh = await PostAsJsonAsync(
            loggedIn, $"{ApiBase}/auth/email/verify", new EmailVerifyRequestPayload(secondToken), authToken))
        {
            Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
        }

        // A resend on the now-verified account answers 200 {status:"verified"} —
        // nothing to resend.
        using (var again = await PostEmptyAsync(loggedIn, $"{ApiBase}/auth/email/verify/resend", authToken))
        {
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
            var againBody = await again.Content.ReadFromJsonAsync<EmailVerificationPayload>();
            Assert.Equal("verified", againBody!.Status);
        }

        loggedIn.Dispose();
        client.Dispose();
    }

    // ---- A11: the verification gate ------------------------------------------

    [Fact]
    public async Task The_email_verified_gate_answers_403_until_the_address_is_confirmed()
    {
        // A11 — the wire shape of the gate: an authenticated-but-unverified
        // principal is 403 EMAIL_NOT_VERIFIED on a verification-gated action
        // (the /_test/verified seam runs the same EnsureEmailVerifiedAsync the
        // RFQ submit use case calls); sign-in itself stays open.
        string email = NewEmail();
        var (client, _) = await RegisterAsync(_factory, email);
        var (loggedIn, authToken) = await LoginNewClientAsync(_factory, email, Password);

        using (var gated = await loggedIn.GetAsync("/_test/verified"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, gated.StatusCode);
            ErrorEnvelope envelope = await ReadEnvelopeAsync(gated);
            Assert.Equal(ErrorCodes.EmailNotVerified, envelope.Code);
        }

        string token = await ReadVerificationEmailTokenAsync(email);
        using (var verified = await PostAsJsonAsync(
            loggedIn, $"{ApiBase}/auth/email/verify", new EmailVerifyRequestPayload(token), authToken))
        {
            Assert.Equal(HttpStatusCode.OK, verified.StatusCode);
        }

        using var open = await loggedIn.GetAsync("/_test/verified");
        Assert.Equal(HttpStatusCode.OK, open.StatusCode);

        loggedIn.Dispose();
        client.Dispose();
    }

    // ---- The reset email rides the same outbox channel -----------------------

    [Fact]
    public async Task A_reset_request_queues_the_reset_email_carrying_the_same_token()
    {
        // 1.38 added the identity.password.reset_requested outbox event: the
        // payload's token is the SAME single-use value the 202 response returned —
        // both channels deliver one token, so the email and the SPA never diverge.
        string email = NewEmail();
        var (client, requestToken) = await RegisterAsync(_factory, email);

        using var response = await PostAsJsonAsync(
            client, $"{ApiBase}/auth/password/reset-request", new PasswordResetRequestPayload(email), requestToken);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<PasswordResetRequestedPayload>();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StockpileDbContext>();
        OutboxMessage row = await db.OutboxMessages
            .Where(message => message.EventType == "identity.password.reset_requested")
            .Where(message => message.Payload.Contains(email))
            .OrderByDescending(message => message.Id)
            .FirstAsync();
        using var payload = JsonDocument.Parse(row.Payload);
        Assert.Equal(body!.RequestToken, payload.RootElement.GetProperty("token").GetString());

        client.Dispose();
    }

    // ---- Helpers -------------------------------------------------------------

    private static async Task<HttpResponseMessage> PostAsJsonAsync(
        HttpClient client, string route, object payload, string requestToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, route)
        {
            Content = JsonContent.Create(payload, options: Wire),
        };
        return await AntiforgeryTestSupport.SendUnsafeAsync(client, request, requestToken);
    }

    /// <summary>An unsafe verb with an empty body (the resend endpoint takes none).</summary>
    private static async Task<HttpResponseMessage> PostEmptyAsync(
        HttpClient client, string route, string requestToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, route)
        {
            Content = new ByteArrayContent(Array.Empty<byte>()),
        };
        return await AntiforgeryTestSupport.SendUnsafeAsync(client, request, requestToken);
    }

    /// <summary>Registers the account and returns the client plus the register-minted (anonymous) request token.</summary>
    private static async Task<(HttpClient Client, string RequestToken)> RegisterAsync(
        StockpileApiFactory factory, string email)
    {
        HttpClient client = AntiforgeryTestSupport.HttpsClient(factory);
        string acquired = await AntiforgeryTestSupport.AcquireTokenAsync(client);
        using var response = await PostAsJsonAsync(
            client, $"{ApiBase}/auth/register",
            new RegisterRequestPayload("Test", "Buyer", email, "+94771234567", Password), acquired);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<RegisterResponsePayload>();
        Assert.False(string.IsNullOrWhiteSpace(body!.RequestToken));
        return (client, body.RequestToken!);
    }

    /// <summary>Signs in on a fresh client (asserting 200) and returns the authenticated request token.</summary>
    private static async Task<(HttpClient Client, string RequestToken)> LoginNewClientAsync(
        StockpileApiFactory factory, string email, string password)
    {
        HttpClient client = AntiforgeryTestSupport.HttpsClient(factory);
        string acquired = await AntiforgeryTestSupport.AcquireTokenAsync(client);
        using var response = await PostAsJsonAsync(
            client, $"{ApiBase}/auth/login", new LoginRequestPayload(email, password), acquired);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<LoginPayload>();
        Assert.False(string.IsNullOrWhiteSpace(body!.RequestToken));
        return (client, body.RequestToken!);
    }

    /// <summary>
    /// The opaque verification token from the queued email: the most recent
    /// <c>identity.user.registered</c> outbox row for this address (its payload is
    /// the delivery record the email renderer consumes).
    /// </summary>
    private async Task<string> ReadVerificationEmailTokenAsync(string email)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StockpileDbContext>();
        OutboxMessage row = await db.OutboxMessages
            .Where(message => message.EventType == "identity.user.registered")
            .Where(message => message.Payload.Contains(email))
            .OrderByDescending(message => message.Id)
            .FirstAsync();
        using var payload = JsonDocument.Parse(row.Payload);
        return payload.RootElement.GetProperty("token").GetString()
            ?? throw new InvalidOperationException("The queued verification email carried no token.");
    }

    private static async Task<ErrorEnvelope> ReadEnvelopeAsync(HttpResponseMessage response)
        => await response.Content.ReadFromJsonAsync<ErrorEnvelope>()
            ?? throw new InvalidOperationException("The error response carried no envelope.");

    // ---- Wire shapes ---------------------------------------------------------

    private sealed record RegisterRequestPayload(
        string FirstName, string LastName, string Email, string? Phone, string Password);

    private sealed record RegisterResponsePayload(string? Status, string? RequestToken);

    private sealed record LoginRequestPayload(string Email, string Password);

    private sealed record LoginPayload(string? RequestToken, AuthSessionResponse? User);

    private sealed record EmailVerifyRequestPayload(string Token);

    private sealed record EmailVerificationPayload(string? Status);

    private sealed record PasswordResetRequestPayload(string Email);

    private sealed record PasswordResetRequestedPayload(string? Status, string? RequestToken);
}