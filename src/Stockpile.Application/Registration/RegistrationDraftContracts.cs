namespace Stockpile.Application.Registration;

/// <summary>
/// One autosave of a partially completed registration (business-flows §2 "each
/// step is independently idempotent and resumable via the draftToken"; §3 "one
/// resumable draft"; api.md §2.1 <c>POST /auth/register/supplier</c> "accepts
/// draftToken to resume").
/// <para>
/// A blank/absent <see cref="DraftToken"/> starts a fresh draft; a live one
/// replaces that draft's prefill. There is deliberately NO password member: the
/// draft exists to save typing, never a credential.
/// </para>
/// </summary>
public sealed record SaveRegistrationDraftRequest(
    string Kind,
    string? DraftToken,
    string? Email,
    string? FirstName,
    string? LastName,
    string? Phone,
    string? CompanyName,
    string? RegistrationNumber,
    string? Website,
    IReadOnlyList<string>? Categories);

/// <summary>
/// Resume (api.md §2.1): the draft token and nothing else — the token identifies
/// its draft, so an unknown, expired, retired or foreign-flow token answers one
/// identical 404 and never reveals which.
/// </summary>
public sealed record ResumeRegistrationDraftRequest(string DraftToken);

/// <summary>The flows a draft can belong to (business-flows §2 buyer, §3 supplier).</summary>
public static class RegistrationDraftKinds
{
    public const string Buyer = "buyer";

    public const string Supplier = "supplier";

    public static bool IsKnown(string kind) => kind is Buyer or Supplier;
}
