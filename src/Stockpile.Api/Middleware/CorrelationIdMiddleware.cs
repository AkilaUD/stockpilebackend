using System.Text.RegularExpressions;
using Serilog.Context;

namespace Stockpile.Api.Middleware;

/// <summary>
/// Task 1.18 — gives every request one correlation id: an inbound
/// <c>X-Correlation-Id</c> is honoured when it matches api.md §1.4's shape
/// (8–64 chars of <c>[A-Za-z0-9._-]</c>), otherwise a fresh GUID is generated.
/// The id is pushed onto the Serilog log context, stored for the error
/// envelope, and echoed as a response header.
/// </summary>
public sealed partial class CorrelationIdMiddleware(RequestDelegate next)
{
    /// <summary>Request/response header name (api.md §1.4).</summary>
    public const string HeaderName = "X-Correlation-Id";

    /// <summary>Key under which the id is stored in <see cref="HttpContext.Items"/>.</summary>
    public const string ItemKey = "CorrelationId";

    [GeneratedRegex("^[A-Za-z0-9._-]{8,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdFormat { get; }

    /// <summary>
    /// The id assigned to the current request; falls back to the framework's
    /// trace identifier if this middleware did not run (defensive, e.g. tests
    /// building the context by hand).
    /// </summary>
    public static string CorrelationIdOf(HttpContext context) =>
        context.Items.TryGetValue(ItemKey, out var value) && value is string id
            ? id
            : context.TraceIdentifier;

    /// <summary>Middleware entry point.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        var inbound = context.Request.Headers[HeaderName].ToString().Trim();
        var correlationId = IdFormat.IsMatch(inbound) ? inbound : Guid.NewGuid().ToString();

        context.Items[ItemKey] = correlationId;

        // Set before next(): the id is on the response even if a downstream
        // middleware fails before writing the body.
        context.Response.Headers[HeaderName] = correlationId;

        using (LogContext.PushProperty("CorrelationId", correlationId))
        {
            await next(context);
        }
    }
}
