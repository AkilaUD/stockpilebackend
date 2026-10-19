namespace Stockpile.Contracts.Authentication;

/// <summary>
/// The stored state of a resumable registration draft (business-flows §2/§3,
/// api.md §2.1). Successful save and resume answer the same shape, so a client
/// that already holds a token needs no second call: the response IS the prefill
/// to render. Serialized camelCase; absent members are omitted, never null
/// (api.md §1.4).
/// </summary>
public sealed record RegistrationDraftState(
    string DraftToken,
    string Kind,
    DateTime ExpiresAtUtc,
    RegistrationDraftPrefill Prefill);

/// <summary>
/// Everything a draft remembers: the NON-SECRET fields of the flow's early stages.
/// Names and phone are the person stage, the company block is the business stage,
/// and <see cref="Categories"/> is the coverage stage — never a password and never
/// a verification document.
/// </summary>
public sealed record RegistrationDraftPrefill(
    string? Email,
    string? FirstName,
    string? LastName,
    string? Phone,
    string? CompanyName,
    string? RegistrationNumber,
    string? Website,
    IReadOnlyList<string> Categories);
