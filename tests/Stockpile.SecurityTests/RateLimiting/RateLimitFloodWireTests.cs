using System.Net;
using System.Net.Http.Json;
using Stockpile.Contracts.Errors;
using Stockpile.SecurityTests.Testing;

namespace Stockpile.SecurityTests.RateLimiting;

/// <summary>
/// security-test-matrix A2/R1 and H9 over real HTTP, on a host whose
/// credential budgets are the CANONICAL §13.7 numbers (10 / 5 min login,
/// 3 / hour reset — see <see cref="CredentialBudgetApiFactory"/>). The
/// integration suite's shared host relaxes both to 1000 and its `test.tight`
/// proof only exercises generic middleware mechanics; these tests close the
/// remaining wire gap on the real endpoints with the real budgets.
/// <para>
/// One class, one host (see the factory note): a flood here exhausts the
/// shared `ip:` partition of THIS host's limiters, so every budget a test
/// burns is named in that test and no two tests share a policy window.
/// </para>
/// </summary>
public sealed class RateLimitFloodWireTests : IClassFixture<CredentialBudgetApiFactory>
{
    private readonly CredentialBudgetApiFactory _factory;

    public RateLimitFloodWireTests(CredentialBudgetApiFactory factory) => _factory = factory;

    /// <summary>
    /// A2 / R1 — credential stuffing: a login flood from one client across
    /// MANY accounts (distinct unknown emails, so no single account's
    /// lockout can mask the network control). Ten attempts pass through as
    /// 401 INVALID_CREDENTIALS (identical envelope — no enumeration); the
    /// eleventh trips `auth.login` and answers the api.md §1.8 429 contract:
    /// RATE_LIMITED envelope (retryable, support reference, correlation) and
    /// the four limit headers naming the canonical budget of 10.
    /// </summary>
    [Fact]
    public async Task Login_flood_across_accounts_is_429_after_the_canonical_budget()
    {
        using HttpClient client = SecurityTestClient.HttpsClient(_factory);
        string token = await SecurityTestClient.AcquireTokenAsync(client);

        HttpResponseMessage? rejected = null;
        for (var attempt = 1; attempt <= 11; attempt++)
        {
            using var request = SecurityTestClient.UnsafePost(
                "/api/v1/auth/login",
                new { email = $"a2-flood-{attempt}@attacker.test", password = "Wrong-Password-1!" },
                token);
            var response = await client.SendAsync(request);

            if (attempt < 11)
            {
                using (response)
                {
                    Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
                }
            }
            else
            {
                rejected = response; // escapes the branch: body read below
            }
        }

        using (rejected)
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, rejected!.StatusCode);

            // api.md §1.8 headers naming the canonical auth.login budget.
            Assert.Equal("10", rejected.Headers.GetValues("X-RateLimit-Limit").Single());
            Assert.Equal("0", rejected.Headers.GetValues("X-RateLimit-Remaining").Single());
            Assert.True(rejected.Headers.Contains("Retry-After"));
            Assert.True(rejected.Headers.TryGetValues("X-RateLimit-Reset", out var reset));
            Assert.False(string.IsNullOrWhiteSpace(Assert.Single(reset!)));

            // api.md §1.4/§1.5 envelope — RATE_LIMITED, retryable, correlated.
            var envelope = await rejected.Content.ReadFromJsonAsync<ErrorEnvelope>();
            Assert.NotNull(envelope);
            Assert.Equal(ErrorCodes.RateLimited, envelope!.Code);
            Assert.True(envelope.Retryable);
            Assert.False(string.IsNullOrWhiteSpace(envelope.SupportReference));
            Assert.False(string.IsNullOrWhiteSpace(envelope.CorrelationId));
        }
    }

    /// <summary>
    /// H9 — forwarded headers from an untrusted peer are IGNORED. If the API
    /// honoured `X-Forwarded-For`, every unique value below would mint a
    /// fresh `ip:` rate-limit partition and a flood could reset its own
    /// budget by rotating the header (the classic proxy-trust bypass). The
    /// fourth request trips the shared connection partition of `auth.reset`
    /// (3 / hour) instead — proof the partition reads the socket peer, not
    /// the attacker-controlled header. `X-Forwarded-Proto` rides along: no
    /// ForwardedHeaders middleware is registered, so neither header reaches
    /// any middleware's options.
    /// </summary>
    [Fact]
    public async Task Rotating_forwarded_client_headers_cannot_reset_the_budget()
    {
        using HttpClient client = SecurityTestClient.HttpsClient(_factory);
        string token = await SecurityTestClient.AcquireTokenAsync(client);

        HttpResponseMessage? rejected = null;
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            using var request = SecurityTestClient.UnsafePost(
                "/api/v1/auth/password/reset-request",
                new { email = $"h9-probe-{attempt}@attacker.test" },
                token);
            request.Headers.Add("X-Forwarded-For", $"203.0.113.{attempt}");
            request.Headers.Add("X-Forwarded-Proto", "https");
            var response = await client.SendAsync(request);

            if (attempt < 4)
            {
                using (response)
                {
                    // Any non-429 answer is fine here (the reset flow's own
                    // enumeration contract is PasswordResetApiTests' job) — the
                    // point is that the rotating header does NOT buy a fresh
                    // partition.
                    Assert.NotEqual(HttpStatusCode.TooManyRequests, response.StatusCode);
                }
            }
            else
            {
                rejected = response; // escapes the branch: headers read below
            }
        }

        using (rejected)
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, rejected!.StatusCode);
            Assert.Equal("3", rejected.Headers.GetValues("X-RateLimit-Limit").Single());
        }
    }
}
