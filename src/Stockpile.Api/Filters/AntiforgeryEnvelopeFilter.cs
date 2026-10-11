using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;
using Stockpile.Api.Configuration;
using Stockpile.Api.Errors;
using Stockpile.Api.Middleware;
using Stockpile.Contracts.Errors;

namespace Stockpile.Api.Filters;

/// <summary>
/// Task 1.35 — antiforgery enforcement for the whole API (security-test-matrix
/// C1–C5). Every unsafe verb (anything but GET/HEAD/OPTIONS/TRACE) must carry
/// a valid double-submit token; a missing, tampered, foreign, or Origin-mismatched
/// token answers 400 + ANTIFORGERY_FAILED via the api.md §1.4 envelope with the
/// correlation id echoed — never the framework's bare 400 with a plain text
/// body (which would break the single-on-the-wire-format invariant).
/// <para>
/// The Origin half (C4) is enforced here because
/// <see cref="IAntiforgery.ValidateRequestAsync(HttpContext)"/> binds only the
/// double-submit cookie+header pair — it never consults the Origin header.
/// An Origin (or Referer, the browser's fallback) must match this request's
/// own scheme+host, or name an origin on the CORS allowlist (spec §13.6): the
/// credentialed cross-origin partner is an intentional surface (C8), not a
/// CSRF vector. When neither header is present there is no cross-origin
/// signal to cross-check — the double-submit binding still holds.
/// </para>
/// </summary>
public sealed partial class AntiforgeryEnvelopeFilter : IAsyncAuthorizationFilter
{
    private readonly IAntiforgery _antiforgery;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AntiforgeryEnvelopeFilter> _logger;

    public AntiforgeryEnvelopeFilter(
        IAntiforgery antiforgery, IConfiguration configuration, ILogger<AntiforgeryEnvelopeFilter> logger)
    {
        _antiforgery = antiforgery;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        if (IsSafeVerb(context.HttpContext.Request.Method))
        {
            return;
        }

        try
        {
            await _antiforgery.ValidateRequestAsync(context.HttpContext);
        }
        catch (AntiforgeryValidationException ex)
        {
            // Firewall log: why the unsafe verb was rejected (a stale token after
            // an identity change, a tampered cookie, a missing header…).
            LogRejected(
                _logger,
                ex,
                context.HttpContext.Request.Method,
                context.HttpContext.Request.Path,
                CorrelationIdMiddleware.CorrelationIdOf(context.HttpContext),
                ex.Message);
            await RejectAsync(context);
            return;
        }

        if (!IsSameOrigin(context.HttpContext.Request))
        {
            await RejectAsync(context);
        }
    }

    private static async Task RejectAsync(AuthorizationFilterContext context)
    {
        context.HttpContext.Response.Headers[CorrelationIdMiddleware.HeaderName] =
            CorrelationIdMiddleware.CorrelationIdOf(context.HttpContext);

        var envelope = ErrorEnvelopeFactory.Create(context.HttpContext, ErrorCodes.AntiforgeryFailed);
        var jsonOptions = context.HttpContext.RequestServices
            .GetRequiredService<IOptions<JsonOptions>>().Value.JsonSerializerOptions;

        context.Result = new ContentResult
        {
            StatusCode = StatusCodes.Status400BadRequest,
            ContentType = "application/json; charset=utf-8",
            Content = JsonSerializer.Serialize(envelope, jsonOptions),
        };

        await Task.CompletedTask;
    }

    private bool IsSameOrigin(HttpRequest request)
    {
        // Cross-site requests always carry Origin; same-origin fetch POSTs
        // carry it too. Referer is the browser's fallback for same-site
        // navigations. With neither, there is nothing to cross-check.
        if (!request.Headers.TryGetValue(HeaderNames.Origin, out var originValues)
            || originValues.Count == 0)
        {
            if (request.Headers.TryGetValue(HeaderNames.Referer, out var refererValues)
                && refererValues.Count > 0)
            {
                return AllValuesMatch(refererValues, request);
            }

            return true;
        }

        return AllValuesMatch(originValues, request);
    }

    private bool AllValuesMatch(StringValues values, HttpRequest request)
    {
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (!ValueMatchesRequest(value, request))
            {
                return false;
            }
        }

        return true;
    }

    private bool ValueMatchesRequest(string value, HttpRequest request)
    {
        // Credentialed cross-origin partners are explicit configuration, not
        // an accident: an allowlisted Origin outranks the same-origin rule.
        if (CorsConfiguration.ReadAllowedOrigins(_configuration).Contains(value))
        {
            return true;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var origin) || string.IsNullOrEmpty(origin.Host))
        {
            // "Origin: null" and unparsable origins are never accepted.
            return false;
        }

        // Scheme must match too — an Origin that would downgrade https to http
        // is rejected. Ports are treated as same-site (the platform never
        // serves its API from a non-default port).
        return string.Equals(request.Scheme, origin.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(request.Host.Host, origin.Host, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSafeVerb(string method) =>
        HttpMethods.IsGet(method) || HttpMethods.IsHead(method)
        || HttpMethods.IsOptions(method) || HttpMethods.IsTrace(method);

    [LoggerMessage(
        EventId = 2202,
        Level = LogLevel.Warning,
        Message = "Antiforgery rejected {Method} {Path} (correlation {CorrelationId}): {Reason}")]
    private static partial void LogRejected(
        ILogger logger, Exception exception, string method, string path, string correlationId, string reason);
}
