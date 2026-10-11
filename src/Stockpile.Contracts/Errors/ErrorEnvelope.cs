namespace Stockpile.Contracts.Errors;

/// <summary>
/// Wire shape of every API error (api.md §1.4). Serialized camelCase with
/// absent optional members omitted, never null.
/// </summary>
public sealed record ErrorEnvelope(
    string Code,
    string Message,
    IReadOnlyList<FieldError>? FieldErrors,
    string CorrelationId,
    string SupportReference,
    bool Retryable,
    string Timestamp);

/// <summary>
/// One field-level failure: property path, the validator's machine-readable
/// error code (e.g. <c>NotEmpty</c>), and a human message (§1.4).
/// </summary>
public sealed record FieldError(string Field, string Code, string Message);
