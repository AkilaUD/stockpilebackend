using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Stockpile.Api.Authorization;
using Stockpile.Api.Errors;
using Stockpile.Contracts.Errors;

namespace Stockpile.Api.Configuration;

/// <summary>
/// Task 1.34 — cookie authentication (security-test-matrix A14, threat-model
/// §13.4, architecture §8.1). The API authenticates with a single scheme:
/// <c>CookieAuthenticationDefaults.AuthenticationScheme</c>. Every cookie flag
/// is fixed at secure values, never configurable downward: <c>HttpOnly</c>,
/// <c>SameSite=Lax</c>, and a <c>SecurePolicy</c> of "always on HTTPS, else
/// SameAsRequest" (Production REFUSES to boot without
/// <c>Authentication:RequireHttps</c>, so the cookie is Always there too).
/// The ticket slides for <c>Authentication:SessionMinutes</c> (default 30) —
/// renewed on activity — while the absolute 12-hour cap and server-side
/// revocation are enforced from task 1.36 by the LoginSession row and an
/// <c>OnValidatePrincipal</c> hook (the cookie mechanism itself only knows the
/// sliding window; a revived session cannot outlive its server record).
/// Because this is an API, the cookie handler's login/access-denied
/// "redirects" are rewritten to the JSON envelope contract (401
/// UNAUTHENTICATED / 403 INSUFFICIENT_CAPABILITY, api.md §1.4/§5) — the
/// frontend never follows a Location header for auth.
/// </summary>
public static class AuthenticationConfiguration
{
    /// <summary>Cookie name (architecture §8.1).</summary>
    public const string CookieName = ".Stockpile.Auth";

    /// <summary>
    /// The second HttpOnly cookie (task 1.40, architecture §8.1): the rotating
    /// refresh token, persistent until its row expires. Same flag discipline
    /// as the session cookie — HttpOnly, SameSite=Lax, Secure in step with
    /// <see cref="RequireHttpsKey"/>.
    /// </summary>
    public const string RefreshCookieName = ".Stockpile.Refresh";

    /// <summary>Sliding ticket window, minutes (architecture §8.1, default 30).</summary>
    public const string SessionMinutesKey = "Authentication:SessionMinutes";

    /// <summary>
    /// When true the session cookie is <c>SecurePolicy.Always</c>; when false it
    /// is <c>SameAsRequest</c> (Development over plain HTTP). Production refuses
    /// a false value — cookies must be Always there (A14).
    /// </summary>
    public const string RequireHttpsKey = "Authentication:RequireHttps";

    public const int DefaultSessionMinutes = 30;

    /// <summary>Upper bound for the sliding window: 12 h (architecture §8.1 absolute cap).</summary>
    public const int MaxSessionMinutes = 12 * 60;

    /// <summary>
    /// Registers cookie authentication with the secure, fixed flags above.
    /// Throws <see cref="InvalidOperationException"/> (the host will not boot)
    /// when the session window is outside [1, 720] minutes or when Production
    /// would issue cookies without the Secure attribute.
    /// </summary>
    public static void AddStockpileCookieAuthentication(
        IServiceCollection services, IConfiguration configuration, string environmentName)
    {
        int sessionMinutes = ReadSessionMinutes(configuration);
        bool requireHttps = configuration.GetValue(RequireHttpsKey, false);

        bool isProduction =
            string.Equals(environmentName, Environments.Production, StringComparison.OrdinalIgnoreCase);
        if (isProduction && !requireHttps)
        {
            throw new InvalidOperationException(
                $"'{RequireHttpsKey}' must be true in Production: the session cookie must carry the Secure "
                + $"attribute on every response (security-test-matrix A14, architecture §8.1).");
        }

        services
            .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.Cookie.Name = CookieName;
                options.Cookie.HttpOnly = true; // A14 — never script-readable
                options.Cookie.SameSite = SameSiteMode.Lax; // A14 — CSRF-resistant, safe top-level nav
                options.Cookie.SecurePolicy = requireHttps
                    ? CookieSecurePolicy.Always
                    : CookieSecurePolicy.SameAsRequest; // A14 — Always in Production (enforced above)
                options.Cookie.Path = "/";
                options.SlidingExpiration = true; // architecture §8.1
                options.ExpireTimeSpan = TimeSpan.FromMinutes(sessionMinutes);

                // API contract: authentication is never a redirect. Missing or
                // invalid ticket -> 401 + UNAUTHENTICATED envelope; insufficient
                // capability -> 403 + INSUFFICIENT_CAPABILITY envelope (api.md
                // §1.4, §5). The frontend maps the codes, never a Location.
                // Task 1.36: OnValidatePrincipal makes every request consult the
                // server-side LoginSession row, so a revoked or expired session
                // (logout, logout-all, session revoke, the 12 h absolute cap)
                // stops working immediately — A7/A15. The rejection code is
                // carried through HttpContext.Items so the challenge below
                // answers SESSION_EXPIRED rather than the generic
                // UNAUTHENTICATED (the two share the 401 status; api.md §5).
                options.Events = new CookieAuthenticationEvents
                {
                    OnRedirectToLogin = context =>
                        WriteAuthenticationEnvelope(context, StatusCodes.Status401Unauthorized, RejectionCodeOf(context.HttpContext)),
                    OnRedirectToAccessDenied = context =>
                        WriteAuthenticationEnvelope(context, StatusCodes.Status403Forbidden, ErrorCodes.InsufficientCapability),
                    OnValidatePrincipal = ValidateSessionAsync,
                };
            });

        // ---- Task 1.41/1.42: the five-layer pipeline's layer 2 -------------
        // Base authorization service plus the deny-by-default FLOOR (threat-model
        // T6.1): the fallback policy demands a signed-in caller on any endpoint
        // that declares no authorization metadata at all — an anonymous probe is
        // challenged (401) before any action body runs. The floor is the
        // DenyByDefaultRequirement rather than RequireAuthenticatedUser because
        // the framework applies the fallback to ANY request whose metadata
        // combines to nothing, endpoint or not (AuthorizationPolicy.CombineAsync's
        // fallback branch): routing's own refusals — a 404 with no endpoint, and
        // the synthesized 405 rejection endpoint (empty metadata, delegate writes
        // only Allow + 405) — reach it too, and deny-anonymous would challenge
        // them into a bogus 401 clobbering routing's status. The requirement
        // passes those two through and denies everything else. The F10/X2
        // architecture test separately forbids shipping an undeclared controller
        // action — every public surface marks itself [AllowAnonymous] explicitly
        // — so the floor is defense in depth.
        services.AddAuthorization(options =>
        {
            options.FallbackPolicy = new AuthorizationPolicyBuilder()
                .AddRequirements(DenyByDefaultRequirement.Instance)
                .Build();

            // Task 1.44 — the MFA policy hook (threat-model §13.4 line 174,
            // §12 "enforcement policy added when rollout is decided"): the
            // policy is REGISTERED for real — PermissionPolicyProvider answers
            // it as-is (branch 1) and MfaAuthorizationHandler gates on the
            // account's fresh TwoFactorEnabled — but phase 1 attaches it to no
            // shipped action; the rollout decides where it applies (and ships
            // the UI), so today only the /_test/mfa-gate seam exercises it.
            options.AddPolicy(MfaRequirement.PolicyName, policy => policy
                .RequireAuthenticatedUser()
                .AddRequirements(MfaRequirement.Instance));
        });

        // The permission provider replaces the framework default (AddAuthorization
        // TryAdds its own, so the descriptor is swapped rather than appended).
        // [Authorize(Policy = "<permission code>")] then builds the capability
        // policy; the handlers are stateless singletons that resolve the
        // permission store from the request's own scope, so neither the root
        // provider's scope validation nor a fresh request can see stale grants.
        services.Replace(
            ServiceDescriptor.Singleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>());
        services.AddSingleton<IAuthorizationHandler, PermissionAuthorizationHandler>();
        services.AddSingleton<IAuthorizationHandler, DenyAuthorizationHandler>();
        services.AddSingleton<IAuthorizationHandler, DenyByDefaultAuthorizationHandler>();
        services.AddSingleton<IAuthorizationHandler, MfaAuthorizationHandler>();

        // The permission handler reads the ambient request (its scope owns the
        // permission store); the accessor is a TryAdd singleton, safe to
        // request again even if another component already registered it.
        services.AddHttpContextAccessor();
    }

    private static int ReadSessionMinutes(IConfiguration configuration)
    {
        int? configured = configuration.GetValue<int?>(SessionMinutesKey);
        if (configured is null)
        {
            return DefaultSessionMinutes;
        }

        if (configured.Value is < 1 or > MaxSessionMinutes)
        {
            throw new InvalidOperationException(
                $"'{SessionMinutesKey}' must be between 1 and {MaxSessionMinutes} minutes "
                + $"(default {DefaultSessionMinutes}); got {configured.Value}. The window must stay "
                + $"short-lived and can never exceed the 12-hour absolute cap "
                + $"(security-test-matrix A14, architecture §8.1).");
        }

        return configured.Value;
    }

    private static Task WriteAuthenticationEnvelope(
        RedirectContext<CookieAuthenticationOptions> context, int statusCode, string errorCode)
    {
        context.Response.StatusCode = statusCode;
        context.Response.Headers.Remove("Location"); // neutralize the redirect

        var envelope = ErrorEnvelopeFactory.Create(context.HttpContext, errorCode);
        var jsonOptions = context.HttpContext.RequestServices
            .GetRequiredService<IOptions<Microsoft.AspNetCore.Mvc.JsonOptions>>().Value.JsonSerializerOptions;
        context.Response.ContentType = "application/json; charset=utf-8";
        return context.Response.WriteAsync(
            System.Text.Json.JsonSerializer.Serialize(envelope, jsonOptions), context.HttpContext.RequestAborted);
    }

    /// <summary>
    /// Item key carrying the envelope code decided during ticket validation, so
    /// the challenge that follows a <c>RejectPrincipal</c> can answer
    /// <c>SESSION_EXPIRED</c> (server row revoked or expired — A7, A15) instead
    /// of the generic <c>UNAUTHENTICATED</c> reserved for "no ticket at all".
    /// Lives in <c>HttpContext.Items</c>, which is per-request and never crosses
    /// requests or responses.
    /// </summary>
    private const string RejectionCodeItem = "Stockpile.Auth.RejectionCode";

    private static string RejectionCodeOf(HttpContext httpContext)
        => httpContext.Items[RejectionCodeItem] as string ?? ErrorCodes.Unauthenticated;

    /// <summary>
    /// Task 1.36 — the server-side revocation + absolute-cap check. Tickets
    /// without a <c>sid</c> claim (pre-1.36 tickets, the <c>/_test/signin</c>
    /// seam) are not server-session bound and pass through untouched: the cookie
    /// mechanism remains the sliding window for those, exactly as before.
    /// Otherwise the responsible <see cref="LoginSession"/> row is loaded and
    /// the ticket dies the moment the row is revoked or past its absolute
    /// <c>ExpiresAt</c> (architecture §8.1: the ticket can never outlive its
    /// server record). Live sessions get a throttled heartbeat write.
    /// </summary>
    private static async Task ValidateSessionAsync(CookieValidatePrincipalContext context)
    {
        if (context.Principal?.FindFirst(Stockpile.Application.Authentication.AuthConstants.SessionIdClaim)
                is not { } sid
            || !Guid.TryParse(sid.Value, out Guid sessionId))
        {
            // No server-side session claim — nothing to validate.
            return;
        }

        IServiceProvider services = context.HttpContext.RequestServices;
        var store = services.GetRequiredService<Stockpile.Application.Authentication.ILoginSessionStore>();
        var session = await store.FindAsync(sessionId, context.HttpContext.RequestAborted);
        DateTime nowUtc = services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime;

        if (session is null || session.RevokedAt is not null || session.ExpiresAt <= nowUtc)
        {
            context.HttpContext.Items[RejectionCodeItem] = ErrorCodes.SessionExpired;
            context.RejectPrincipal();
            return;
        }

        // Sliding-heartbeat marker, throttled to once per minute per session
        // (domain-model §2.5; the session list's "last seen" column).
        await store.TouchAsync(sessionId, nowUtc, TimeSpan.FromMinutes(1), context.HttpContext.RequestAborted);
    }
}
