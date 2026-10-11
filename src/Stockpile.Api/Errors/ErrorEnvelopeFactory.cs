using System.Globalization;
using System.Text.Json;
using Stockpile.Api.Middleware;
using Stockpile.Contracts.Errors;

namespace Stockpile.Api.Errors;

/// <summary>
/// The one construction point for api.md §1.4 error envelopes — the exception
/// handler, validation filter, rate limiter, and idempotency check all emit
/// through here so the shape and the SUP- support reference never drift.
/// </summary>
internal static class ErrorEnvelopeFactory
{
    private static readonly ErrorDefinition Fallback = ErrorCodes.Resolve(ErrorCodes.InternalError)
        ?? throw new InvalidOperationException($"'{ErrorCodes.InternalError}' is missing from ErrorCodes.All.");

    /// <summary>HTTP status for a registered code; 500 for anything unknown (a bug).</summary>
    internal static int StatusCodeOf(string code) =>
        ErrorCodes.Resolve(code)?.HttpStatusCode ?? StatusCodes.Status500InternalServerError;

    /// <summary>
    /// Builds the envelope for <paramref name="code"/>. Explicit message, field
    /// errors, and retryability win over the registry defaults; absent code
    /// registrations fall back to INTERNAL_ERROR's defaults while keeping the
    /// original code visible.
    /// </summary>
    internal static ErrorEnvelope Create(
        HttpContext httpContext,
        string code,
        string? message = null,
        IReadOnlyList<FieldError>? fieldErrors = null,
        bool? retryable = null)
    {
        var definition = ErrorCodes.Resolve(code);

        return new ErrorEnvelope(
            Code: code,
            Message: !string.IsNullOrWhiteSpace(message)
                ? message
                : definition?.DefaultMessage ?? Fallback.DefaultMessage,
            FieldErrors: fieldErrors,
            CorrelationId: CorrelationIdMiddleware.CorrelationIdOf(httpContext),
            SupportReference: NewSupportReference(),
            Retryable: retryable ?? definition?.Retryable ?? false,
            Timestamp: DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Wire casing for field paths: CLR property names become the camelCase
    /// paths clients send and expect (api.md §1.4: "items[2].quantity"), each
    /// dot-segment camel-cased because <see cref="JsonNamingPolicy.CamelCase"/>
    /// only lowers the first character of the whole string. Model-binding paths
    /// ("$.name") lose their JSON-root prefix. Empty, null, or bare "$" maps to
    /// "request" for failures that span the whole payload.
    /// </summary>
    internal static string WireFieldName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name == "$")
        {
            return "request";
        }

        if (name.StartsWith("$.", StringComparison.Ordinal))
        {
            name = name[2..];
        }

        return string.Join('.', name.Split('.').Select(JsonNamingPolicy.CamelCase.ConvertName));
    }

    /// <summary>
    /// Validator codes on the wire: FluentValidation's default code is the
    /// validator's class name ("NotEmptyValidator") — the trailing "Validator"
    /// is framework detail clients shouldn't see, so it is trimmed. Codes set
    /// explicitly by a validator pass through unchanged. Absent codes map to
    /// "Invalid", matching the model-binding path.
    /// </summary>
    internal static string WireFieldCode(string? code)
    {
        if (string.IsNullOrEmpty(code))
        {
            return "Invalid";
        }

        const string suffix = "Validator";
        return code.EndsWith(suffix, StringComparison.Ordinal)
            ? code[..^suffix.Length]
            : code;
    }

    /// <summary>Short, quotable support code (api.md §1.4); logged with its stack trace.</summary>
    internal static string NewSupportReference() => "SUP-" + Guid.NewGuid().ToString("N")[..6];
}
