using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Stockpile.IntegrationTests.Testing;

/// <summary>
/// TestServer has no transport, so it reports NO peer address — and
/// <c>ForwardedHeadersMiddleware</c> applies the forwarded headers UNCHECKED
/// when the peer is unknown ("For the first instance, allow remoteIp to be
/// null for servers that don't support it natively"). Every real server
/// (Kestrel, IIS/ANCM) reports one, so this factory gives every request the
/// peer a transport would, and the trust decision in
/// <c>ReverseProxyConfiguration</c> is exercised the way a deployment
/// exercises it (threat-model §18, security-test-matrix H9).
/// <para>
/// The address itself is arbitrary here: this host declares "any address" as
/// the edge. <see cref="Stockpile.SecurityTests.Testing.PeerReportingHttpContextFactory"/>
/// is the same class for the security suite, whose host declares the opposite
/// case.
/// </para>
/// </summary>
internal sealed class PeerReportingHttpContextFactory : IHttpContextFactory
{
    private readonly DefaultHttpContextFactory _inner;
    private readonly IPAddress _peer;

    internal PeerReportingHttpContextFactory(IServiceProvider services, IPAddress peer)
    {
        _inner = new DefaultHttpContextFactory(services);
        _peer = peer;
    }

    public HttpContext Create(IFeatureCollection featureCollection)
    {
        HttpContext context = _inner.Create(featureCollection);
        context.Connection.RemoteIpAddress ??= _peer;
        return context;
    }

    public void Dispose(HttpContext httpContext) => _inner.Dispose(httpContext);
}
