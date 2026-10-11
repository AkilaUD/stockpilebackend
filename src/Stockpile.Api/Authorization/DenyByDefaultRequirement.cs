using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace Stockpile.Api.Authorization;

/// <summary>
/// The single requirement of the deny-by-default FALLBACK policy (threat-model
/// T6.1, security-test-matrix X2): any endpoint that matched routing but
/// declares no authorization metadata demands a signed-in caller — an
/// anonymous probe gets 401, never the action body.
/// <para>
/// Two kinds of request pass through without a decision, because routing —
/// not endpoint code — owns their response (the framework applies the fallback
/// to ANY request whose metadata combines to nothing, endpoint or not, so a
/// blanket deny-anonymous would clobber both):
/// <list type="bullet">
/// <item>a request NO endpoint matched (a routing 404);</item>
/// <item>the framework's synthesized 405 rejection endpoint — the only
/// endpoint ASP.NET creates with completely empty metadata (its delegate
/// writes nothing but the Allow header and the 405 status), so an empty
/// metadata collection identifies routing machinery rather than an
/// application surface that could leak a resource.</item>
/// </list>
/// Everything else — any real endpoint lacking an explicit decision — fails
/// closed here and is challenged by the middleware (401 anonymous, 403
/// signed-in). The F10/X2 architecture test independently forbids shipping a
/// controller action without a decision; this is the runtime floor beneath
/// it. The floor REPLACES <c>RequireAuthenticatedUser</c> rather than joining
/// it: requirements are ANDed, and deny-anonymous would fail the two routing
/// cases before this carve-out could run (each carve-out is covered by
/// AntiforgeryApiTests.C5 and FunctionAuthorizationApiTests).
/// </para>
/// </summary>
public sealed class DenyByDefaultRequirement : IAuthorizationRequirement
{
    /// <summary>Stateless singleton: the requirement carries no per-call data.</summary>
    public static DenyByDefaultRequirement Instance { get; } = new();

    private DenyByDefaultRequirement()
    {
    }
}

/// <summary>
/// Handles <see cref="DenyByDefaultRequirement"/>: pass routing's own
/// refusals through, demand a signed-in caller on every real endpoint.
/// </summary>
public sealed class DenyByDefaultAuthorizationHandler : AuthorizationHandler<DenyByDefaultRequirement>
{
    /// <inheritdoc />
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, DenyByDefaultRequirement requirement)
    {
        bool routingOwnsResponse = context.Resource switch
        {
            // The framework's normal resource: the request. Its endpoint is
            // null (404, nothing matched) or metadata-empty (the synthesized
            // 405 rejection endpoint) → routing is answering, permit it.
            HttpContext httpContext => IsRoutingRefusal(httpContext.GetEndpoint()),

            // The endpoint itself (only with the resource-switch AppContext
            // flag): same emptiness test, no request needed.
            Endpoint endpoint => IsRoutingRefusal(endpoint),

            // Unknown resource shape → fail closed: demand a signed-in caller.
            _ => false,
        };

        if (routingOwnsResponse || context.User.Identity?.IsAuthenticated == true)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }

    private static bool IsRoutingRefusal(Endpoint? endpoint) =>
        endpoint is null || endpoint.Metadata.Count == 0;
}
