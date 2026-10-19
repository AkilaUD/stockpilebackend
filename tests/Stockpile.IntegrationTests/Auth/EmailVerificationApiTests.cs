using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Stockpile.Contracts.Authentication;
using Stockpile.Domain.Entities.Platform;
using Stockpile.Infrastructure.Persistence;
using Stockpile.IntegrationTests.Testing;

namespace Stockpile.IntegrationTests.Auth;

/// <summary>
/// Registration accepts email and password without sending a verification
/// message; password-reset email delivery remains covered here as a separate flow.
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

    // ---- Registration does not require email verification ------------------

    [Fact]
    public async Task Register_activates_buyer_without_queuing_verification_email()
    {
        string email = NewEmail();
        var (client, _) = await RegisterAsync(_factory, email);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StockpileDbContext>();
            var user = await db.Users.SingleAsync(row => row.Email == email);
            Assert.NotNull(user.EmailConfirmedAt);
            Assert.False(await db.EmailVerificationTokens.AnyAsync(row => row.UserId == user.PublicId));
            Assert.False(await db.OutboxMessages
                .AnyAsync(message => message.EventType == "identity.user.registered"
                    && message.Payload.Contains(email)));
        }

        var (loggedIn, _) = await LoginNewClientAsync(_factory, email, Password);
        using var gate = await loggedIn.GetAsync("/_test/verified");
        Assert.Equal(HttpStatusCode.OK, gate.StatusCode);

        client.Dispose();
        loggedIn.Dispose();
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

    /// <summary>Registers the account and returns the client plus the register-minted (anonymous) request token.</summary>
    private static async Task<(HttpClient Client, string RequestToken)> RegisterAsync(
        StockpileApiFactory factory, string email)
    {
        HttpClient client = AntiforgeryTestSupport.HttpsClient(factory);
        string acquired = await AntiforgeryTestSupport.AcquireTokenAsync(client);
        using var response = await PostAsJsonAsync(
            client, $"{ApiBase}/auth/register",
            new RegisterRequestPayload(email, Password), acquired);
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

    // ---- Wire shapes ---------------------------------------------------------

    private sealed record RegisterRequestPayload(string Email, string Password);

    private sealed record RegisterResponsePayload(string? Status, string? RequestToken);

    private sealed record LoginRequestPayload(string Email, string Password);

    private sealed record LoginPayload(string? RequestToken, AuthSessionResponse? User);

    private sealed record PasswordResetRequestPayload(string Email);

    private sealed record PasswordResetRequestedPayload(string? Status, string? RequestToken);
}