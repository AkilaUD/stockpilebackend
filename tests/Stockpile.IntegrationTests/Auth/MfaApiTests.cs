using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Stockpile.Application.Authentication;
using Stockpile.Contracts.Errors;
using Stockpile.Domain.Entities.Identity;
using Stockpile.Infrastructure.Persistence;
using Stockpile.IntegrationTests.Testing;

namespace Stockpile.IntegrationTests.Auth;

/// <summary>
/// Task 1.44 — the MFA-ready endpoints over real HTTP (threat-model §13.4
/// line 174, T1.8, api.md §2.1): enrol mints a pending TOTP secret whose ONLY
/// appearance is the enrol response (no-store — the body is credential
/// material), verify is the single writer of <c>TwoFactorEnabled</c>, and the
/// registered <c>mfa.required</c> policy — proven through the
/// <c>/_test/mfa-gate</c> seam — answers 403 until that flip and 200 after.
/// Enforcement itself is deliberately absent (§12 "architecture ready, not
/// enforced"): sign-in never challenges, and no shipped action attaches the
/// policy. Codes are computed with the SAME <see cref="Totp"/> the server
/// runs, so the assertions pin the RFC 6238 behaviour end-to-end rather than
/// a mocked clock.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class MfaApiTests
{
    private readonly StockpileApiFactory _factory;

    /// <summary>Shares the one API host (single migrate + seed of Stockpile_Test).</summary>
    public MfaApiTests(StockpileApiFactory factory) => _factory = factory;

    private const string Password = "correct-horse-battery-staple";
    private const string ApiBase = "/api/v1";

    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    private static string NewEmail() => $"mfa.{Guid.NewGuid():N}@test.local";

    // ---- Enrol → pending → verify → enabled, with the policy hook in between --

    [Fact]
    public async Task Enrolling_mints_a_pending_secret_and_a_verified_code_enables_the_hook()
    {
        string email = NewEmail();
        var (loggedIn, authToken) = await RegisterAndLoginAsync(_factory, email);

        // Enrol: 200 with the secret exactly once — pending, not enabled.
        using (var enrolled = await PostEmptyAsync(loggedIn, $"{ApiBase}/auth/mfa/enroll", authToken))
        {
            Assert.Equal(HttpStatusCode.OK, enrolled.StatusCode);
            Assert.True(enrolled.Headers.CacheControl?.NoStore == true,
                "the enrol body carries credential material — it must never be cached (§1.10)");
            MfaEnrollPayload? body = await enrolled.Content.ReadFromJsonAsync<MfaEnrollPayload>();
            Assert.False(string.IsNullOrWhiteSpace(body!.Secret));
            Assert.Equal(32, body.Secret!.Length);
            Assert.StartsWith($"otpauth://totp/Stockpile:{Uri.EscapeDataString(email)}?secret=", body.OtpauthUri);
        }

        User pending = await ReadUserAsync(email);
        Assert.False(pending.TwoFactorEnabled, "an unverified pairing must never be live");
        Assert.False(string.IsNullOrEmpty(pending.TwoFactorSecret));

        // The hook denies the unverified account over real HTTP.
        using (var denied = await loggedIn.GetAsync("/_test/mfa-gate"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            ErrorEnvelope envelope = await ReadEnvelopeAsync(denied);
            Assert.Equal(ErrorCodes.InsufficientCapability, envelope.Code);
        }

        // Verify with the code the RFC 6238 algorithm produces for the secret.
        string code = Totp.ComputeCode(pending.TwoFactorSecret!, DateTimeOffset.UtcNow);
        using (var verified = await PostAsJsonAsync(
            loggedIn, $"{ApiBase}/auth/mfa/verify", new MfaVerifyRequestPayload(code), authToken))
        {
            Assert.Equal(HttpStatusCode.OK, verified.StatusCode);
            MfaVerifyPayload? body = await verified.Content.ReadFromJsonAsync<MfaVerifyPayload>();
            Assert.Equal("enabled", body!.Status);
        }

        User enabled = await ReadUserAsync(email);
        Assert.True(enabled.TwoFactorEnabled);

        // Same policy, now satisfied — the hook is real on both sides.
        using var open = await loggedIn.GetAsync("/_test/mfa-gate");
        Assert.Equal(HttpStatusCode.OK, open.StatusCode);

        loggedIn.Dispose();
    }

    // ---- Failure shapes: 422 field errors, one code each ---------------------

    [Fact]
    public async Task A_wrong_or_malformed_code_is_a_422_field_error_and_enables_nothing()
    {
        string email = NewEmail();
        var (loggedIn, authToken) = await RegisterAndLoginAsync(_factory, email);
        string secret = await EnrollAsync(loggedIn, authToken);

        // Wrong but well-formed: one digit perturbed from the real code.
        string real = Totp.ComputeCode(secret, DateTimeOffset.UtcNow);
        char flipped = real[0] == '0' ? '1' : '0';
        using (var wrong = await PostAsJsonAsync(
            loggedIn, $"{ApiBase}/auth/mfa/verify", new MfaVerifyRequestPayload($"{flipped}{real[1..]}"), authToken))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, wrong.StatusCode);
            ErrorEnvelope envelope = await ReadEnvelopeAsync(wrong);
            Assert.Equal(ErrorCodes.ValidationFailed, envelope.Code);
            FieldError error = Assert.Single(envelope.FieldErrors!);
            Assert.Equal("code", error.Field);
            Assert.Equal("invalid_code", error.Code);
        }

        // Malformed shape — the request filter answers first with the SAME
        // stable field code, so the client never sees two codes for "bad code".
        using (var malformed = await PostAsJsonAsync(
            loggedIn, $"{ApiBase}/auth/mfa/verify", new MfaVerifyRequestPayload("abcdef"), authToken))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, malformed.StatusCode);
            ErrorEnvelope envelope = await ReadEnvelopeAsync(malformed);
            Assert.Equal(ErrorCodes.ValidationFailed, envelope.Code);
            FieldError error = Assert.Single(envelope.FieldErrors!);
            Assert.Equal("code", error.Field);
            Assert.Equal("invalid_code", error.Code);
        }

        Assert.False((await ReadUserAsync(email)).TwoFactorEnabled);
        loggedIn.Dispose();
    }

    [Fact]
    public async Task Verifying_without_an_enrolment_is_a_not_enrolled_field_error()
    {
        string email = NewEmail();
        var (loggedIn, authToken) = await RegisterAndLoginAsync(_factory, email);

        using var response = await PostAsJsonAsync(
            loggedIn, $"{ApiBase}/auth/mfa/verify", new MfaVerifyRequestPayload("123456"), authToken);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        ErrorEnvelope envelope = await ReadEnvelopeAsync(response);
        Assert.Equal(ErrorCodes.ValidationFailed, envelope.Code);
        FieldError error = Assert.Single(envelope.FieldErrors!);
        Assert.Equal("code", error.Field);
        Assert.Equal("not_enrolled", error.Code);

        loggedIn.Dispose();
    }

    [Fact]
    public async Task Verifying_an_already_enabled_account_is_defensively_idempotent()
    {
        string email = NewEmail();
        var (loggedIn, authToken) = await RegisterAndLoginAsync(_factory, email);
        string secret = await EnrollAsync(loggedIn, authToken);
        await VerifyAsync(loggedIn, authToken, secret);

        // A repeat answer is stable even with a code that would be wrong — the
        // account is enabled, there is nothing left to prove (email-verify's
        // already_verified discipline).
        using var again = await PostAsJsonAsync(
            loggedIn, $"{ApiBase}/auth/mfa/verify", new MfaVerifyRequestPayload("999999"), authToken);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        MfaVerifyPayload? body = await again.Content.ReadFromJsonAsync<MfaVerifyPayload>();
        Assert.Equal("already_enabled", body!.Status);

        loggedIn.Dispose();
    }

    // ---- Re-enrolment: the lost-device recovery path -------------------------

    [Fact]
    public async Task Re_enrolling_replaces_the_secret_and_returns_the_account_to_pending()
    {
        string email = NewEmail();
        var (loggedIn, authToken) = await RegisterAndLoginAsync(_factory, email);
        string firstSecret = await EnrollAsync(loggedIn, authToken);
        await VerifyAsync(loggedIn, authToken, firstSecret);

        // Re-enrol (lost device): a FRESH pending secret — the old pairing dies.
        string secondSecret = await EnrollAsync(loggedIn, authToken);
        Assert.NotEqual(firstSecret, secondSecret);
        User pending = await ReadUserAsync(email);
        Assert.False(pending.TwoFactorEnabled, "an unverified replacement must never be live");
        Assert.Equal(secondSecret, pending.TwoFactorSecret);

        // The old authenticator can no longer verify — its secret is gone.
        using (var stale = await PostAsJsonAsync(
            loggedIn, $"{ApiBase}/auth/mfa/verify",
            new MfaVerifyRequestPayload(Totp.ComputeCode(firstSecret, DateTimeOffset.UtcNow)), authToken))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, stale.StatusCode);
        }

        await VerifyAsync(loggedIn, authToken, secondSecret);
        Assert.True((await ReadUserAsync(email)).TwoFactorEnabled);
        loggedIn.Dispose();
    }

    // ---- Authentication floor + fail-closed hook -----------------------------

    [Fact]
    public async Task The_mfa_endpoints_demand_a_signed_in_caller()
    {
        using var client = AntiforgeryTestSupport.HttpsClient(_factory);
        string requestToken = await AntiforgeryTestSupport.AcquireTokenAsync(client);

        using (var enroll = await PostEmptyAsync(client, $"{ApiBase}/auth/mfa/enroll", requestToken))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, enroll.StatusCode);
            ErrorEnvelope envelope = await ReadEnvelopeAsync(enroll);
            Assert.Equal(ErrorCodes.Unauthenticated, envelope.Code);
        }

        using var verify = await PostAsJsonAsync(
            client, $"{ApiBase}/auth/mfa/verify", new MfaVerifyRequestPayload("123456"), requestToken);
        Assert.Equal(HttpStatusCode.Unauthorized, verify.StatusCode);
    }

    [Fact]
    public async Task The_mfa_policy_hook_fails_closed_for_anonymous_and_for_a_subjectless_principal()
    {
        // Anonymous: the policy's RequireAuthenticatedUser challenges first.
        using var anonymous = AntiforgeryTestSupport.HttpsClient(_factory);
        using (var denied = await anonymous.GetAsync("/_test/mfa-gate"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            ErrorEnvelope envelope = await ReadEnvelopeAsync(denied);
            Assert.Equal(ErrorCodes.Unauthenticated, envelope.Code);
        }

        // The fixed /_test/signin seam principal is authenticated but carries no
        // `sub` claim — there is no account to read TwoFactorEnabled from, so
        // the handler denies (403), exactly like the permission handler: no
        // resolution path is never a pass.
        using var seam = AntiforgeryTestSupport.HttpsClient(_factory);
        using (await seam.GetAsync("/_test/signin"))
        {
            // the fixed seam cookie is established (its shape is 1.34's contract)
        }

        using var deniedSeam = await seam.GetAsync("/_test/mfa-gate");
        Assert.Equal(HttpStatusCode.Forbidden, deniedSeam.StatusCode);
        ErrorEnvelope seamEnvelope = await ReadEnvelopeAsync(deniedSeam);
        Assert.Equal(ErrorCodes.InsufficientCapability, seamEnvelope.Code);
    }

    // ---- Helpers -------------------------------------------------------------

    /// <summary>Enrols and returns the freshly minted pending secret.</summary>
    private static async Task<string> EnrollAsync(HttpClient client, string requestToken)
    {
        using var response = await PostEmptyAsync(client, $"{ApiBase}/auth/mfa/enroll", requestToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        MfaEnrollPayload? body = await response.Content.ReadFromJsonAsync<MfaEnrollPayload>();
        return body!.Secret!;
    }

    /// <summary>Verifies the code for <paramref name="secret"/> and asserts the enabled flip.</summary>
    private static async Task VerifyAsync(HttpClient client, string requestToken, string secret)
    {
        string code = Totp.ComputeCode(secret, DateTimeOffset.UtcNow);
        using var response = await PostAsJsonAsync(
            client, $"{ApiBase}/auth/mfa/verify", new MfaVerifyRequestPayload(code), requestToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        MfaVerifyPayload? body = await response.Content.ReadFromJsonAsync<MfaVerifyPayload>();
        Assert.Equal("enabled", body!.Status);
    }

    private async Task<User> ReadUserAsync(string email)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StockpileDbContext>();
#pragma warning disable CA1862 // `ToUpperInvariant()` translates to SQL UPPER(); StringComparison overloads do not
        return await db.Users.SingleAsync(user => user.EmailNormalized == email.ToUpperInvariant());
#pragma warning restore CA1862
    }

    private static async Task<HttpResponseMessage> PostAsJsonAsync(
        HttpClient client, string route, object payload, string requestToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, route)
        {
            Content = JsonContent.Create(payload, options: Wire),
        };
        return await AntiforgeryTestSupport.SendUnsafeAsync(client, request, requestToken);
    }

    /// <summary>An unsafe verb with an empty body (enrol takes none).</summary>
    private static async Task<HttpResponseMessage> PostEmptyAsync(
        HttpClient client, string route, string requestToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, route)
        {
            Content = new ByteArrayContent(Array.Empty<byte>()),
        };
        return await AntiforgeryTestSupport.SendUnsafeAsync(client, request, requestToken);
    }

    /// <summary>Registers the account, then signs in on a fresh client (register does not sign anyone in).</summary>
    private static async Task<(HttpClient Client, string RequestToken)> RegisterAndLoginAsync(
        StockpileApiFactory factory, string email)
    {
        using HttpClient registerClient = AntiforgeryTestSupport.HttpsClient(factory);
        string anonymousToken = await AntiforgeryTestSupport.AcquireTokenAsync(registerClient);
        using var registered = await PostAsJsonAsync(
            registerClient, $"{ApiBase}/auth/register",
            new RegisterRequestPayload("Test", "Mfa", email, "+94771234567", Password), anonymousToken);
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);

        HttpClient client = AntiforgeryTestSupport.HttpsClient(factory);
        string acquired = await AntiforgeryTestSupport.AcquireTokenAsync(client);
        using var loggedInResponse = await PostAsJsonAsync(
            client, $"{ApiBase}/auth/login", new LoginRequestPayload(email, Password), acquired);
        Assert.Equal(HttpStatusCode.OK, loggedInResponse.StatusCode);
        LoginPayload? body = await loggedInResponse.Content.ReadFromJsonAsync<LoginPayload>();
        return (client, body!.RequestToken!);
    }

    private static async Task<ErrorEnvelope> ReadEnvelopeAsync(HttpResponseMessage response)
        => await response.Content.ReadFromJsonAsync<ErrorEnvelope>()
            ?? throw new InvalidOperationException("The error response carried no envelope.");

    // ---- Wire shapes ---------------------------------------------------------

    private sealed record RegisterRequestPayload(
        string FirstName, string LastName, string Email, string? Phone, string Password);

    private sealed record LoginRequestPayload(string Email, string Password);

    private sealed record LoginPayload(string? RequestToken);

    private sealed record MfaEnrollPayload(string? Secret, string? OtpauthUri);

    /// <summary>The verify REQUEST body — the one field the endpoint takes.</summary>
    private sealed record MfaVerifyRequestPayload(string Code);

    /// <summary>The verify RESPONSE body — status only.</summary>
    private sealed record MfaVerifyPayload(string? Status);
}
