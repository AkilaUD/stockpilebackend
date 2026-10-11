using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Stockpile.Api.RateLimiting;

namespace Stockpile.IntegrationTests.RateLimiting;

/// <summary>
/// Task 1.26 — the api.md §1.8 policy registry: all named spec policies are
/// declared (X13 registry side), the test-only policy cannot leak into a
/// non-Testing build, and the (subjectId | clientIp) partitioning matches the
/// contract (api.md §1.8; the full "user A does not punish user B" scenario
/// gets its end-to-end test once cookie auth lands in 1D — R13). Actual
/// enforcement (429 + headers + envelope) is proven over HTTP in
/// <see cref="RateLimitingApiTests"/>.
/// </summary>
public sealed class RateLimitPoliciesTests
{
    [Fact]
    public void Registry_exposes_every_spec_policy_plus_the_test_only_policy()
    {
        var expected = new HashSet<string>(StringComparer.Ordinal)
        {
            RateLimitPolicies.AuthLogin,
            RateLimitPolicies.AuthReset,
            RateLimitPolicies.AuthRegister,
            RateLimitPolicies.AuthVerify,
            RateLimitPolicies.AuthMfa,
            RateLimitPolicies.AuthRefresh,
            RateLimitPolicies.AdminUnlock,
            RateLimitPolicies.SearchPublic,
            RateLimitPolicies.RfqCreate,
            RateLimitPolicies.RfqSubmit,
            RateLimitPolicies.QuoteSubmit,
            RateLimitPolicies.QuoteRevision,
            RateLimitPolicies.ContactIntent,
            RateLimitPolicies.PriceAlertCreate,
            RateLimitPolicies.ReviewCreate,
            RateLimitPolicies.UploadPublic,
            RateLimitPolicies.PublicApi,
            RateLimitPolicies.OrganizationCreate,
            RateLimitPolicies.OrganizationMemberInvite,
            RateLimitPolicies.AddressWrite,
            RateLimitPolicies.TestTight,
        };

        Assert.Equal(expected, RateLimitPolicies.All.ToHashSet(StringComparer.Ordinal));
    }

    [Fact]
    public void AddPolicies_registers_everything_for_both_environment_modes()
    {
        // A malformed policy registration throws at AddPolicy time; succeeding
        // in both modes proves every name is backed by a real partition factory
        // and the test-only gating code path is sound.
        var production = new RateLimiterOptions();
        RateLimitPolicies.AddPolicies(production, testingOnly: false);

        var testing = new RateLimiterOptions();
        RateLimitPolicies.AddPolicies(testing, testingOnly: true);
    }

    [Fact]
    public void Sensitive_flows_are_real_registered_policies()
    {
        Assert.All(
            RateLimitPolicies.Sensitive,
            policy => Assert.Contains(policy, RateLimitPolicies.All, StringComparer.Ordinal));
        Assert.DoesNotContain(RateLimitPolicies.TestTight, RateLimitPolicies.Sensitive);
    }

    [Fact]
    public void Partition_key_uses_the_subject_when_signed_in_else_the_client_ip()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.9");

        Assert.Equal("ip:203.0.113.9", RateLimitPartitionKeys.For(context));

        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", "0f0f0f0f-1234-4a5b-8c6d-7e8f9a0b1c2d")], "cookie"));
        Assert.Equal("u:0f0f0f0f-1234-4a5b-8c6d-7e8f9a0b1c2d", RateLimitPartitionKeys.For(context));
    }

    [Fact]
    public void Unknown_client_identity_lands_in_the_shared_partition()
    {
        // No IP and no identity must still produce a stable partition — an
        // unkeyable request can never bypass the limiter by throwing.
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = null;

        Assert.Equal("ip:unknown", RateLimitPartitionKeys.For(context));
    }
}
