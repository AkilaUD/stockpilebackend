using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// Both System.Net and Microsoft.AspNetCore.HttpOverrides export an IPNetwork
// (the latter is the pre-net8 shim); the forwarded-header options speak the
// System.Net one in this framework version.
using IPNetwork = System.Net.IPNetwork;

namespace Stockpile.Api.Configuration;

/// <summary>
/// Threat-model §18 — every deployed topology puts a TLS-terminating reverse
/// proxy in front of the API (the Vercel edge that fronts MonsterASP in
/// production, the platform gateway in front of an app service). That proxy is
/// the only party that knows the public scheme, the visitor's host and the
/// visitor's address; it publishes them as <c>X-Forwarded-Proto</c>,
/// <c>X-Forwarded-Host</c> and <c>X-Forwarded-For</c>, and this host adopts them
/// — but ONLY from a peer the operator has declared
/// (security-test-matrix H9: forwarded headers from an untrusted address are
/// ignored).
/// <para>
/// Without this, a deploy whose origin hop is plain HTTP could not be correct
/// about itself: antiforgery token minting threw
/// (<c>DefaultAntiforgery.CheckSSLConfig</c> — "SecurePolicy = Always, but the
/// current request is not an SSL request"), so every sign-in and registration
/// answered 500 with an opaque support reference; the session cookie lost its
/// Secure attribute; and rate limiting and audit recorded the proxy's address
/// as the caller's, collapsing every visitor into one bucket.
/// </para>
/// <para>
/// Trust is declared, never inferred. <c>ReverseProxy:KnownProxies</c> lists
/// proxy addresses and <c>ReverseProxy:KnownNetworks</c> lists CIDR ranges.
/// With neither set the framework default stands — loopback only — so an
/// undeclared deployment ignores forwarded headers entirely rather than
/// honouring whatever arrives. A configured value that does not parse stops
/// startup: a typo must never silently widen or narrow the trust boundary.
/// </para>
/// </summary>
public static partial class ReverseProxyConfiguration
{
    /// <summary>Configuration key listing the proxy addresses trusted to set the forwarded headers.</summary>
    public const string KnownProxiesKey = "ReverseProxy:KnownProxies";

    /// <summary>Configuration key listing the proxy networks (CIDR) trusted to set the forwarded headers.</summary>
    public const string KnownNetworksKey = "ReverseProxy:KnownNetworks";

    /// <summary>
    /// Bound on distinct peers the plain-HTTP diagnostic names per process —
    /// enough to expose a misdeclared proxy (and a scanner), never a log flood.
    /// </summary>
    private const int MaxReportedPeers = 8;

    /// <summary>Marker for a forwarded header the request did not carry.</summary>
    private const string Absent = "(absent)";

    /// <summary>
    /// Registers forwarded-header processing (threat-model §18,
    /// security-test-matrix H9). Called before the host is built;
    /// <see cref="UseStockpileReverseProxy"/> places the middleware.
    /// </summary>
    public static void AddStockpileReverseProxy(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<ForwardedHeadersOptions>(options => Apply(configuration, options));
    }

    /// <summary>
    /// Puts forwarded-header processing at the head of the pipeline and, when
    /// the deployment declares HTTPS, names each peer that still reaches the
    /// API over plain HTTP — the exact condition under which antiforgery
    /// minting throws and sign-in answers 500, so the operator reads the cause
    /// and the remedy instead of hunting a correlation id.
    /// </summary>
    public static IApplicationBuilder UseStockpileReverseProxy(
        this IApplicationBuilder app, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(configuration);

        bool requireHttps = configuration.GetValue(AuthenticationConfiguration.RequireHttpsKey, false);

        // First real middleware, so correlation ids, request logging, rate-limit
        // partitioning, antiforgery and authentication all observe the visitor
        // rather than the proxy — the partitions in RateLimitPartitionKeys key on
        // that address (task 1.26), and the antiforgery Origin check compares
        // against the request's own scheme and host
        // (Filters/AntiforgeryEnvelopeFilter).
        app.UseForwardedHeaders();

        if (requireHttps)
        {
            ILogger logger = app.ApplicationServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger(typeof(ReverseProxyConfiguration).FullName!);
            var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            app.Use(async (context, next) =>
            {
                if (!context.Request.IsHttps && context.Connection.RemoteIpAddress is { } peer)
                {
                    bool first;
                    lock (reported)
                    {
                        first = reported.Count < MaxReportedPeers && reported.Add(peer.ToString());
                    }

                    if (first)
                    {
                        LogPlainRequestWhileHttpsRequired(
                            logger,
                            peer.ToString(),
                            Header(context, "X-Forwarded-Proto"),
                            Header(context, "X-Forwarded-For"),
                            Header(context, "X-Forwarded-Host"));
                    }
                }

                await next(context);
            });
        }

        return app;
    }

    /// <summary>
    /// The trust boundary itself (threat-model §18): the header set, the single
    /// expected hop, and the declared proxy set. Exposed to tests so the
    /// accepted and ignored cases are asserted without a live proxy.
    /// </summary>
    internal static void Apply(IConfiguration configuration, ForwardedHeadersOptions options)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(options);

        // The proxy is the edge that terminates TLS for the public surface, so
        // exactly one hop is expected. XForwardedHost rides along because the
        // app's own idea of its host must be the visitor's: the antiforgery
        // Origin check compares an Origin against request.Scheme + request.Host.
        options.ForwardedHeaders =
            ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
        options.ForwardLimit = 1;

        string[] proxies = ReadList(configuration, KnownProxiesKey);
        string[] networks = ReadList(configuration, KnownNetworksKey);

        if (proxies.Length == 0 && networks.Length == 0)
        {
            // Nothing declared: the framework default (loopback only) stands, so
            // an undeclared proxy is an untrusted peer. Trusting headers because
            // they asked to be trusted is exactly the H9 defect.
            return;
        }

        // The declared set replaces the default outright — the operator's list is
        // the whole list, so nothing carried in from the framework can widen it.
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();

        foreach (string value in proxies)
        {
            options.KnownProxies.Add(ParseProxy(value));
        }

        foreach (string value in networks)
        {
            options.KnownIPNetworks.Add(ParseNetwork(value));
        }
    }

    private static string[] ReadList(IConfiguration configuration, string key) =>
        (configuration.GetSection(key).Get<string[]>() ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .ToArray();

    private static IPAddress ParseProxy(string value)
    {
        if (!IPAddress.TryParse(value, out IPAddress? address))
        {
            throw new InvalidOperationException(
                $"'{KnownProxiesKey}' contains '{value}', which is not an IP address. Forwarded headers are "
                + "trusted only from the proxies named here (threat-model §18, security-test-matrix H9), so a "
                + "value that cannot be parsed would leave the deployment in a state nobody chose. Use an "
                + "address such as 203.0.113.9, or a range in '" + KnownNetworksKey + "'.");
        }

        return address;
    }

    private static IPNetwork ParseNetwork(string value)
    {
        if (!IPNetwork.TryParse(value, out IPNetwork network))
        {
            throw new InvalidOperationException(
                $"'{KnownNetworksKey}' contains '{value}', which is not an IP network in CIDR form. Forwarded "
                + "headers are trusted only from the proxies named here (threat-model §18, "
                + "security-test-matrix H9), so a value that cannot be parsed would leave the deployment in a "
                + "state nobody chose. Use a range such as 10.0.0.0/8 or ::/0.");
        }

        return network;
    }

    private static string Header(HttpContext context, string name) =>
        context.Request.Headers.TryGetValue(name, out var value) ? value.ToString() : Absent;

    [LoggerMessage(
        EventId = 2402,
        Level = LogLevel.Warning,
        Message = "Request from {Peer} reached the API over plain HTTP while Authentication:RequireHttps is true, "
            + "so the public surface is HTTPS and this hop is not. Forwarded headers as received: "
            + "X-Forwarded-Proto '{Proto}', X-Forwarded-For '{ForwardedFor}', X-Forwarded-Host '{ForwardedHost}'. "
            + "If {Peer} is the TLS-terminating proxy in front of this API (the deployed topology), declare it in "
            + "ReverseProxy:KnownProxies or ReverseProxy:KnownNetworks — an undeclared peer's headers are ignored, "
            + "and antiforgery token minting answers 500 until the public scheme is known "
            + "(threat-model §18, security-test-matrix H9).")]
    private static partial void LogPlainRequestWhileHttpsRequired(
        ILogger logger, string peer, string proto, string forwardedFor, string forwardedHost);
}
