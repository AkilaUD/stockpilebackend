using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Stockpile.SecurityTests.Testing;

/// <summary>
/// The security suite's own API host — deliberately separate from the
/// integration suite's shared factory (security-test-matrix A2/R1, H9):
/// <list type="bullet">
/// <item>its own process and its own limiter instances, so a flood that
/// exhausts the canonical credential budgets can never poison the
/// integration suite's relaxed host, the dev server on :5080, or any e2e
/// browser session;</item>
/// <item><c>RateLimiting__RelaxUnderTesting=false</c>, so `auth.login` runs
/// at its real 10 / 5 minutes and `auth.reset` at 3 / hour — the exact
/// budgets the matrix rows assert over HTTP. The shared integration host
/// relaxes those to 1000 (RateLimitPolicies.RelaxedUnderTesting) and thus
/// cannot.</item>
/// </list>
/// One host per assembly (<c>IClassFixture</c>): a second
/// WebApplicationFactory in one process freezes Serilog's process-wide
/// ReloadableLogger — "The logger is already frozen" (task 1.24 note, the
/// reason the integration suite collects into <c>ApiCollection</c>).
/// Connection discipline mirrors the integration factory: Testing can only
/// ever reach Stockpile_Test, never Stockpile_Dev (architecture §9.2).
/// </summary>
public sealed class CredentialBudgetApiFactory : WebApplicationFactory<Program>
{
    /// <summary>
    /// The peer every request arrives from (TEST-NET-2, RFC 5737) — never the
    /// declared proxy below. TestServer reports no peer of its own, and a real
    /// transport always does; see <see cref="PeerReportingHttpContextFactory"/>.
    /// </summary>
    private static readonly IPAddress TestPeer = IPAddress.Parse("198.51.100.7");

    static CredentialBudgetApiFactory()
    {
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");
        Environment.SetEnvironmentVariable(
            "ConnectionStrings__Stockpile",
            "Server=(localdb)\\MSSQLLocalDB;Database=Stockpile_Test;Integrated Security=true;TrustServerCertificate=true");
        Environment.SetEnvironmentVariable("Database__AutoMigrate", "true");

        // The point of this host: no relaxation. auth.login = 10 / 5 min,
        // auth.reset = 3 / hour (RateLimitPolicies.Specs — the canonical
        // §13.7 numbers).
        Environment.SetEnvironmentVariable("RateLimiting__RelaxUnderTesting", "false");

        // H9 — trust is declared, so this host declares a proxy no test request
        // can come from. The flood's peer is therefore an untrusted address and
        // its forwarded headers are ignored because the trust check refuses
        // them, not because no middleware is registered (threat-model §18).
        Environment.SetEnvironmentVariable("ReverseProxy__KnownProxies__0", "203.0.113.9");
    }

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureServices(services =>
        {
            // TestServer's requests arrive with no peer address, which is the
            // one case ForwardedHeadersMiddleware applies the forwarded headers
            // without checking the declared proxies. This host's whole H9
            // assertion is about that check, so report the peer a transport
            // would report and let the middleware make the decision.
            services.RemoveAll<IHttpContextFactory>();
            services.AddSingleton<IHttpContextFactory>(
                provider => new PeerReportingHttpContextFactory(provider, TestPeer));
        });
    }
}
