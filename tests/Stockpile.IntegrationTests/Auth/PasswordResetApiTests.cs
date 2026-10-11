using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Stockpile.Contracts.Authentication;
using Stockpile.Contracts.Errors;
using Stockpile.IntegrationTests.Testing;

namespace Stockpile.IntegrationTests.Auth;

/// <summary>
/// Task 1.37 — password reset request/complete and self-service password
/// change over real HTTP (security-test-matrix A4–A6, threat-model T1.5,
/// api.md §2.1): the request step answers 202 with an identical shape for
/// known and unknown accounts (A4, no enumeration); completion is single-use
/// (the security stamp rotates — A5), revokes every session, and clears the
/// lockout; an expired token is rejected with one actionable 400 (A6); a
/// changed password revokes every OTHER device while keeping this one.
/// <para>
/// Antiforgery follows the §1.10 flow exactly: each unsafe verb carries the
/// token minted under the principal the request presents — the anonymous mint
/// for the (anonymous) reset endpoints, the authenticated one for password
/// change.
/// </para>
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class PasswordResetApiTests
{
    private readonly StockpileApiFactory _factory;

    /// <summary>Shares the one API host (single migrate + seed of Stockpile_Test).</summary>
    public PasswordResetApiTests(StockpileApiFactory factory) => _factory = factory;

    private const string Password = "correct-horse-battery-staple";
    private const string ChangedPassword = "changed-horse-battery-staple";
    private const string ApiBase = "/api/v1";

    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    private static string NewEmail() => $"reset.{Guid.NewGuid():N}@test.local";

    // ---- A4: request step never enumerates ---------------------------------

    [Fact]
    public async Task Reset_request_answers_202_identically_for_known_and_unknown_emails()
    {
        // A4 — the request step cannot leak account existence: same status, same
        // field presence, token-shaped answer for a known AND an unknown account.
        using var client = AntiforgeryTestSupport.HttpsClient(_factory);
        string token = await AntiforgeryTestSupport.AcquireTokenAsync(client);
        string knownEmail = NewEmail();
        await RegisterAsync(_factory, knownEmail);

        using var known = await PostAsJsonAsync(
            client, $"{ApiBase}/auth/password/reset-request", new PasswordResetRequestPayload(knownEmail), token);
        using var unknown = await PostAsJsonAsync(
            client, $"{ApiBase}/auth/password/reset-request", new PasswordResetRequestPayload(NewEmail()), token);

        Assert.Equal(HttpStatusCode.Accepted, known.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, unknown.StatusCode);

        var knownBody = await known.Content.ReadFromJsonAsync<PasswordResetRequestedPayload>();
        var unknownBody = await unknown.Content.ReadFromJsonAsync<PasswordResetRequestedPayload>();

        Assert.Equal(knownBody!.Status, unknownBody!.Status);
        Assert.Equal(
            string.IsNullOrWhiteSpace(knownBody.RequestToken),
            string.IsNullOrWhiteSpace(unknownBody.RequestToken));
        Assert.False(string.IsNullOrWhiteSpace(knownBody.RequestToken));
        Assert.False(string.IsNullOrWhiteSpace(unknownBody.RequestToken));
    }

    // ---- A5: single-use, revocation on completion, stale token replay -------

    [Fact]
    public async Task A_completed_reset_signs_out_every_device_and_changes_the_password()
    {
        string email = NewEmail();
        await RegisterAsync(_factory, email);
        var (deviceA, loginToken) = await LoginNewClientAsync(_factory, email, Password);
        await AssertSessionReadableAsync(deviceA);

        // Forgot the password; complete the reset with a new one.
        string resetToken = await RequestResetAsync(deviceA, loginToken, email);
        string newPassword = $"{ChangedPassword}";
        using (var completed = await CompleteResetAsync(deviceA, loginToken, email, resetToken, newPassword))
        {
            Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
            var body = await completed.Content.ReadFromJsonAsync<PasswordResetCompletedPayload>();
            Assert.Equal("password_reset", body!.Status);
        }

        // A5 — the device's ticket died with its server-side session row.
        using (var stale = await deviceA.GetAsync($"{ApiBase}/auth/session"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);
            ErrorEnvelope envelope = await ReadEnvelopeAsync(stale);
            Assert.Equal(ErrorCodes.SessionExpired, envelope.Code);
        }

        // The old password is gone…
        var (oldClient, oldLogin) = await PostLoginNewClientAsync(_factory, email, Password);
        using (oldLogin)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, oldLogin.StatusCode);
            ErrorEnvelope envelope = await ReadEnvelopeAsync(oldLogin);
            Assert.Equal(ErrorCodes.InvalidCredentials, envelope.Code);
        }

        oldClient.Dispose();

        // …the new one signs in.
        var (recoveredClient, recoveredToken) = await LoginNewClientAsync(_factory, email, newPassword);
        Assert.NotNull(recoveredToken);
        recoveredClient.Dispose();
    }

    [Fact]
    public async Task A_reset_token_is_single_use()
    {
        // A5 — the security stamp rotates on completion, so the same token can
        // never be presented twice: the replay answers the same 400 as any
        // invalid token (the reason is never revealed).
        string email = NewEmail();
        var (client, registerToken) = await RegisterAsync(_factory, email);

        string resetToken = await RequestResetAsync(client, registerToken, email);
        using (var first = await CompleteResetAsync(client, registerToken, email, resetToken, ChangedPassword))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        using var replay = await CompleteResetAsync(client, registerToken, email, resetToken, ChangedPassword);
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        ErrorEnvelope envelope = await ReadEnvelopeAsync(replay);
        Assert.Equal(ErrorCodes.ResetTokenInvalid, envelope.Code);
    }

    // ---- A6: short TTL -----------------------------------------------------

    [Fact]
    public async Task An_expired_reset_token_is_rejected_with_an_actionable_error()
    {
        // A6 — the Testing host's DataProtectorTokenProvider TTL is ~10 s
        // (Auth__PasswordResetTokenMinutes=0.17), so the REAL validation path
        // exercises expiry over HTTP: mint, wait past the TTL, complete.
        string email = NewEmail();
        await RegisterAsync(_factory, email);
        var (client, loginToken) = await LoginNewClientAsync(_factory, email, Password);

        string resetToken = await RequestResetAsync(client, loginToken, email);
        await Task.Delay(TimeSpan.FromSeconds(12));

        using var expired = await CompleteResetAsync(client, loginToken, email, resetToken, ChangedPassword);
        Assert.Equal(HttpStatusCode.BadRequest, expired.StatusCode);
        ErrorEnvelope envelope = await ReadEnvelopeAsync(expired);
        Assert.Equal(ErrorCodes.ResetTokenInvalid, envelope.Code);
        // The registry message is actionable: request a new one.
        Assert.Contains("invalid or has expired", envelope.Message);
    }

    // ---- A4 negative + lockout recovery ------------------------------------

    [Fact]
    public async Task Reset_for_an_unknown_email_returns_an_inert_token_that_cannot_complete()
    {
        // The 202 body for an unknown account is token-SHAPED (A4) but inert:
        // completing with it can never be distinguished from an invalid token.
        using var client = AntiforgeryTestSupport.HttpsClient(_factory);
        string token = await AntiforgeryTestSupport.AcquireTokenAsync(client);

        string inertToken = await RequestResetAsync(client, token, NewEmail());
        using var completed = await CompleteResetAsync(client, token, NewEmail(), inertToken, ChangedPassword);

        Assert.Equal(HttpStatusCode.BadRequest, completed.StatusCode);
        ErrorEnvelope envelope = await ReadEnvelopeAsync(completed);
        Assert.Equal(ErrorCodes.ResetTokenInvalid, envelope.Code);
    }

    [Fact]
    public async Task A_locked_account_recovers_via_a_completed_password_reset()
    {
        // A1 interplay — the lockout (5 failed sign-ins) is a denial until the
        // reset path releases it: a completed reset clears the count, so the
        // password signs in immediately. (The full exponential backoff + jitter
        // refinement is task 1.39.)
        string email = NewEmail();
        var (client, registerToken) = await RegisterAsync(_factory, email);

        for (int attempt = 0; attempt < 5; attempt++)
        {
            using var wrong = await PostAsJsonAsync(
                client, $"{ApiBase}/auth/login",
                new LoginRequestPayload(email, "definitely-not-the-password"), registerToken);
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }

        // Even a correct password is rejected while the account is locked.
        using (var lockedLogin = await PostAsJsonAsync(
            client, $"{ApiBase}/auth/login", new LoginRequestPayload(email, Password), registerToken))
        {
            Assert.Equal(HttpStatusCode.Forbidden, lockedLogin.StatusCode);
            ErrorEnvelope envelope = await ReadEnvelopeAsync(lockedLogin);
            Assert.Equal(ErrorCodes.AccountLocked, envelope.Code);
        }

        // Reset is the recovery path: it clears the lockout.
        string resetToken = await RequestResetAsync(client, registerToken, email);
        using (var completed = await CompleteResetAsync(client, registerToken, email, resetToken, ChangedPassword))
        {
            Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        }

        var (recoveredClient, recoveredToken) = await LoginNewClientAsync(_factory, email, ChangedPassword);
        Assert.NotNull(recoveredToken);
        recoveredClient.Dispose();
    }

    // ---- Password change ---------------------------------------------------

    [Fact]
    public async Task Password_change_requires_the_current_password_and_keeps_this_device()
    {
        string email = NewEmail();
        await RegisterAsync(_factory, email);
        var (deviceA, loginTokenA) = await LoginNewClientAsync(_factory, email, Password);
        var (deviceB, _) = await LoginNewClientAsync(_factory, email, Password);
        await AssertSessionReadableAsync(deviceB);

        // A wrong current password is a field error on the current password.
        using (var wrongChange = await PostAsJsonAsync(
            deviceA, $"{ApiBase}/auth/password/change",
            new PasswordChangeRequestPayload("wrong-current", ChangedPassword), loginTokenA))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, wrongChange.StatusCode);
            ErrorEnvelope envelope = await ReadEnvelopeAsync(wrongChange);
            Assert.Equal(ErrorCodes.ValidationFailed, envelope.Code);
            FieldError fieldError = Assert.Single(envelope.FieldErrors!);
            Assert.Equal("currentPassword", fieldError.Field);
        }

        using var changed = await PostAsJsonAsync(
            deviceA, $"{ApiBase}/auth/password/change",
            new PasswordChangeRequestPayload(Password, ChangedPassword), loginTokenA);
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);

        // THIS device stays signed in…
        using (var stillMine = await deviceA.GetAsync($"{ApiBase}/auth/session"))
        {
            Assert.Equal(HttpStatusCode.OK, stillMine.StatusCode);
        }

        // …but the other device's ticket died with its server-side session row.
        using (var otherDevice = await deviceB.GetAsync($"{ApiBase}/auth/session"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, otherDevice.StatusCode);
            ErrorEnvelope envelope = await ReadEnvelopeAsync(otherDevice);
            Assert.Equal(ErrorCodes.SessionExpired, envelope.Code);
        }

        // The old password no longer signs in anywhere after the change.
        var (oldClient, oldLogin) = await PostLoginNewClientAsync(_factory, email, Password);
        using (oldLogin)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, oldLogin.StatusCode);
        }

        oldClient.Dispose();

        // The changed password does.
        var (changedClient, changedToken) = await LoginNewClientAsync(_factory, email, ChangedPassword);
        Assert.NotNull(changedToken);
        changedClient.Dispose();
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

    /// <summary>Signs in on a fresh client; returns the client and its raw response (caller asserts the status).</summary>
    private static async Task<(HttpClient Client, HttpResponseMessage Response)> PostLoginNewClientAsync(
        StockpileApiFactory factory, string email, string password)
    {
        HttpClient client = AntiforgeryTestSupport.HttpsClient(factory);
        string acquired = await AntiforgeryTestSupport.AcquireTokenAsync(client);
        HttpResponseMessage response = await PostAsJsonAsync(
            client, $"{ApiBase}/auth/login", new LoginRequestPayload(email, password), acquired);
        return (client, response);
    }

    /// <summary>Signs in on a fresh client (asserting 200) and returns the authenticated request token.</summary>
    private static async Task<(HttpClient Client, string RequestToken)> LoginNewClientAsync(
        StockpileApiFactory factory, string email, string password)
    {
        var (client, response) = await PostLoginNewClientAsync(factory, email, password);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<LoginPayload>();
        Assert.False(string.IsNullOrWhiteSpace(body!.RequestToken));
        return (client, body.RequestToken!);
    }

    private static async Task<string> RequestResetAsync(
        HttpClient client, string requestToken, string email)
    {
        using var response = await PostAsJsonAsync(
            client, $"{ApiBase}/auth/password/reset-request", new PasswordResetRequestPayload(email), requestToken);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<PasswordResetRequestedPayload>();
        Assert.Equal("reset_requested", body!.Status);
        Assert.False(string.IsNullOrWhiteSpace(body.RequestToken), "the 202 body must carry a token for the flow's next step");
        return body.RequestToken!;
    }

    private static Task<HttpResponseMessage> CompleteResetAsync(
        HttpClient client, string requestToken, string email, string token, string newPassword)
        => PostAsJsonAsync(
            client, $"{ApiBase}/auth/password/reset",
            new PasswordResetCompletePayload(email, token, newPassword), requestToken);

    private static async Task<ErrorEnvelope> ReadEnvelopeAsync(HttpResponseMessage response)
        => await response.Content.ReadFromJsonAsync<ErrorEnvelope>()
            ?? throw new InvalidOperationException("The error response carried no envelope.");

    private static async Task AssertSessionReadableAsync(HttpClient client)
    {
        using var session = await client.GetAsync($"{ApiBase}/auth/session");
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);
    }

    // ---- Wire shapes -----------------------------------------------------------

    private sealed record RegisterRequestPayload(
        string FirstName, string LastName, string Email, string? Phone, string Password);

    private sealed record LoginRequestPayload(string Email, string Password);

    private sealed record RegisterResponsePayload(string? Status, string? RequestToken);

    private sealed record LoginPayload(string? RequestToken, AuthSessionResponse? User);

    private sealed record PasswordResetRequestPayload(string Email);

    private sealed record PasswordResetRequestedPayload(string? Status, string? RequestToken);

    private sealed record PasswordResetCompletePayload(string Email, string Token, string NewPassword);

    private sealed record PasswordResetCompletedPayload(string? Status);

    private sealed record PasswordChangeRequestPayload(string CurrentPassword, string NewPassword);
}