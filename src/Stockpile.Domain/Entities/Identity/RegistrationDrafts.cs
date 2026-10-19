namespace Stockpile.Domain.Entities.Identity;

/// <summary>
/// A resumable registration draft (docs/business-flows.md §2 "staged, resumable"
/// and §3 "seven stages, one resumable draft"; api.md §2.1). One row holds the
/// NON-SECRET prefill a visitor has entered so far, addressed by the SHA-256 hash
/// of an opaque <c>draftToken</c> — the same "hashed at rest" discipline as the
/// verification/reset ledgers (threat-model T1.5), so a database breach yields no
/// usable token.
/// <para>
/// A draft is a TRANSIENT convenience, not an identity: it carries no password
/// (a password is never written here, at any stage), it grants nothing, and it is
/// retired the moment the registration it describes completes
/// (<see cref="CompletedAtUtc"/> + <see cref="UserId"/>). Unlike the session and
/// audit ledgers these rows are not append-only facts, so expired rows are swept
/// rather than kept forever.
/// </para>
/// </summary>
public sealed class RegistrationDraft
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>SHA-256 of the opaque draft token, nvarchar(64), unique.</summary>
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>
    /// Which flow the draft belongs to: <c>buyer</c> or <c>supplier</c>. The wire
    /// values are pinned in <c>Stockpile.Application.Registration.RegistrationDraftKinds</c>;
    /// the domain cannot reference them, so a token presented to the other flow is
    /// refused by comparing this column.
    /// </summary>
    public string Kind { get; set; } = string.Empty;

    public string? Email { get; set; }

    public string? FirstName { get; set; }

    public string? LastName { get; set; }

    public string? Phone { get; set; }

    public string? CompanyName { get; set; }

    public string? RegistrationNumber { get; set; }

    public string? Website { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    /// <summary>Live until this instant; an expired draft answers exactly like an unknown one.</summary>
    public DateTime ExpiresAtUtc { get; set; }

    /// <summary>Non-null once the registration completed — the draft is retired, single-use.</summary>
    public DateTime? CompletedAtUtc { get; set; }

    /// <summary>The account the draft became, set together with <see cref="CompletedAtUtc"/>.</summary>
    public Guid? UserId { get; set; }

    /// <summary>Department/category slugs the visitor ticked (supplier stage 5 prefill — resolved against the live taxonomy when the draft resumes).</summary>
    public List<RegistrationDraftCategory> Categories { get; set; } = [];
}

/// <summary>
/// One ticked category on a <see cref="RegistrationDraft"/>. Prefill only: nothing
/// here is a grant, and no row references it once the draft is retired.
/// </summary>
public sealed class RegistrationDraftCategory
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid DraftId { get; set; }

    public string Slug { get; set; } = string.Empty;
}
