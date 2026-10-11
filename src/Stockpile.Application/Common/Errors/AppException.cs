using Stockpile.Contracts.Errors;

namespace Stockpile.Application.Common.Errors;

/// <summary>
/// Raised by a use case for a known business failure. Carries a code from
/// <see cref="ErrorCodes"/>; the API layer alone maps it to an HTTP status and
/// wire envelope (spec §8: "API layer handles transport … and error mapping").
/// Application code never crafts responses.
/// </summary>
public class AppException : Exception
{
    /// <summary>The stable error code (api.md §5) surfaced to clients.</summary>
    public string Code { get; }

    /// <summary>Optional per-field detail rendered as the envelope's fieldErrors.</summary>
    public IReadOnlyList<FieldError>? FieldErrors { get; }

    /// <summary>Fails with the code's registry default message.</summary>
    public AppException(string code)
        : base(ErrorCodes.Resolve(code)?.DefaultMessage ?? code)
    {
        Code = code;
    }

    /// <summary>Fails with a more specific message than the registry default.</summary>
    public AppException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    /// <summary>Fails with a specific message and an inner exception for the logs.</summary>
    public AppException(string code, string message, Exception innerException)
        : base(message, innerException)
    {
        Code = code;
    }

    /// <summary>Business validation failure with per-field context (fieldErrors).</summary>
    public AppException(string code, IReadOnlyList<FieldError> fieldErrors, string? message = null)
        : base(message ?? ErrorCodes.Resolve(code)?.DefaultMessage ?? code)
    {
        Code = code;
        FieldErrors = fieldErrors;
    }
}
