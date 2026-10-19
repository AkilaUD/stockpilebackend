using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Stockpile.Api.Configuration;

// The forwarded-header options are the framework's; both this and
// Microsoft.AspNetCore.HttpOverrides export an IPNetwork, so be explicit.
using IPNetwork = System.Net.IPNetwork;

namespace Stockpile.IntegrationTests.ReverseProxy;

/// <summary>
/// Threat-model §18 / security-test-matrix H9 — the forwarded-header trust
/// boundary, asserted on the exact middleware the host runs. The suite host
/// declares "any address" because the deployed edge (the Vercel proxy in front
/// of MonsterASP) has no address range to declare, so the IGNORED half of H9
/// has to be proven here, where the peer address is chosen by the test, and the
/// ACCEPTED half is proven over real HTTP in <see cref="ReverseProxyApiTests"/>.
/// </summary>
public sealed class ForwardedHeaderTrustTests
{
    private const string Peer = "198.51.100.7";
    private const string VisitorAddress = "203.0.113.50";
    private const string PublicHost = "stockpilefrontend.vercel.app";

    [Fact]
    public void Apply_WithoutADeclaration_TrustsLoopbackOnly()
    {
        ForwardedHeadersOptions options = new();

        ReverseProxyConfiguration.Apply(Configuration(), options);

        // Nothing declared means the framework default stands — loopback only —
        // so an undeclared proxy is untrusted rather than self-declared.
        Assert.True(IsTrusted(options, IPAddress.Loopback));
        Assert.True(IsTrusted(options, IPAddress.IPv6Loopback));
        Assert.False(IsTrusted(options, IPAddress.Parse(Peer)));
    }

    [Fact]
    public void Apply_WithADeclaredProxy_ReplacesTheDefaultSetEntirely()
    {
        ForwardedHeadersOptions options = new();

        ReverseProxyConfiguration.Apply(
            Configuration(("ReverseProxy:KnownProxies:0", Peer)), options);

        Assert.True(IsTrusted(options, IPAddress.Parse(Peer)));
        // The operator's list is the whole list: the loopback default is gone,
        // so nothing carried in from the framework can widen the trust boundary.
        Assert.False(IsTrusted(options, IPAddress.Loopback));
        Assert.False(IsTrusted(options, IPAddress.IPv6Loopback));
    }

    [Fact]
    public void Apply_WithADeclaredNetwork_ParsesCidrAndReplacesTheDefault()
    {
        ForwardedHeadersOptions options = new();

        ReverseProxyConfiguration.Apply(
            Configuration(("ReverseProxy:KnownNetworks:0", "10.0.0.0/8")), options);

        Assert.True(IsTrusted(options, IPAddress.Parse("10.1.2.3")));
        Assert.False(IsTrusted(options, IPAddress.Parse(Peer)));
        Assert.False(IsTrusted(options, IPAddress.Loopback));
    }

    [Fact]
    public void Apply_WithAMalformedProxy_RefusesToStartNamingTheKey()
    {
        ForwardedHeadersOptions options = new();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
        {
            ReverseProxyConfiguration.Apply(
                Configuration(("ReverseProxy:KnownProxies:0", "not-an-address")), options);
        });

        Assert.Contains(ReverseProxyConfiguration.KnownProxiesKey, error.Message, StringComparison.Ordinal);
        Assert.Contains("not-an-address", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_WithAMalformedNetwork_RefusesToStartNamingTheKey()
    {
        ForwardedHeadersOptions options = new();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
        {
            ReverseProxyConfiguration.Apply(
                Configuration(("ReverseProxy:KnownNetworks:0", "not-a-network")), options);
        });

        Assert.Contains(ReverseProxyConfiguration.KnownNetworksKey, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TrustedPeer_HasItsForwardedSchemeHostAndAddressAdopted()
    {
        HttpContext context = await ThroughMiddlewareAsync(
            Peer,
            Configuration(
                ("ReverseProxy:KnownProxies:0", Peer),
                ("ReverseProxy:KnownNetworks:0", "10.0.0.0/8")),
            request =>
            {
                request.Request.Headers["X-Forwarded-Proto"] = "https";
                request.Request.Headers["X-Forwarded-Host"] = PublicHost;
                request.Request.Headers["X-Forwarded-For"] = VisitorAddress;
            });

        Assert.Equal("https", context.Request.Scheme);
        Assert.True(context.Request.IsHttps);
        Assert.Equal(PublicHost, context.Request.Host.Value);
        Assert.Equal(VisitorAddress, context.Connection.RemoteIpAddress?.ToString());
    }

    /// <summary>
    /// H9 itself: what an undeclared peer claims changes nothing — not the
    /// scheme, not the host, not the address. The request stays plain HTTP, so
    /// the antiforgery guard still refuses to mint and no rate-limit partition
    /// or audit row is written under an address the caller did not have.
    /// </summary>
    [Fact]
    public async Task UntrustedPeer_HasItsForwardedHeadersIgnored()
    {
        HttpContext context = await ThroughMiddlewareAsync(
            Peer,
            Configuration(("ReverseProxy:KnownProxies:0", VisitorAddress)),
            request =>
            {
                request.Request.Headers["X-Forwarded-Proto"] = "https";
                request.Request.Headers["X-Forwarded-Host"] = PublicHost;
                request.Request.Headers["X-Forwarded-For"] = VisitorAddress;
            });

        Assert.Equal("http", context.Request.Scheme);
        Assert.False(context.Request.IsHttps);
        Assert.Equal("origin.test", context.Request.Host.Value);
        Assert.Equal(Peer, context.Connection.RemoteIpAddress?.ToString());
    }

    private static async Task<HttpContext> ThroughMiddlewareAsync(
        string peer, IConfiguration configuration, Action<HttpContext> arrange)
    {
        ForwardedHeadersOptions options = new();
        ReverseProxyConfiguration.Apply(configuration, options);

        var middleware = new ForwardedHeadersMiddleware(
            _ => Task.CompletedTask, NullLoggerFactory.Instance, Options.Create(options));

        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("origin.test");
        arrange(context);

        await middleware.Invoke(context);
        return context;
    }

    /// <summary>
    /// The question the middleware actually asks of its options: may this peer
    /// set forwarded headers? Asked of the option values, so a framework default
    /// that shifts from under us fails the assertion instead of hiding in it.
    /// </summary>
    private static bool IsTrusted(ForwardedHeadersOptions options, IPAddress peer) =>
        options.KnownProxies.Contains(peer)
        || options.KnownIPNetworks.Any(network => network.Contains(peer));

    private static IConfiguration Configuration(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(pair =>
                new KeyValuePair<string, string?>(pair.Key, pair.Value)))
            .Build();
}
