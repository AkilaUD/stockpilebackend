namespace Stockpile.Api.RateLimiting;

/// <summary>
/// api.md §1.8: rate-limit partitions key on the signed-in subject, falling
/// back to the client IP for anonymous traffic. The "sub" claim arrives with
/// cookie auth in phase 1D; until then every request is IP-keyed. The prefix
/// keeps subject and IP keyspaces distinct by construction. A malformed
/// context (no IP, no claim) lands in one shared "unknown" partition rather
/// than throwing — an unlimitable request must never slip past the limiter.
/// </summary>
internal static class RateLimitPartitionKeys
{
    internal static string For(HttpContext httpContext) =>
        httpContext.User.Identity?.IsAuthenticated == true
        && httpContext.User.FindFirst("sub") is { } subject
            ? "u:" + subject.Value
            : "ip:" + (httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown");
}
