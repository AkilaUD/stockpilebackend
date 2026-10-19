using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Stockpile.SecurityTests.Testing;

/// <summary>
/// TestServer has no transport, so it reports NO peer address — and
/// <c>ForwardedHeadersMiddleware</c> applies the forwarded headers UNCHECKED
/// when the peer is unknown ("For the first instance, allow remoteIp to be
/// null for servers that don't support it natively"). Every real server
/// (Kestrel, IIS/ANCM) reports one, so this factory gives every request the
/// peer a transport would. The trust decision is then made rather than
/// skipped: this host declares a proxy it can never be, so the H9 refusal is
/// exercised the way a deployment exercises it — threat-model §18,
/// security-test-matrix H9.
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
