using Microsoft.Extensions.Options;
using Stockpile.Application.Authentication;
using Stockpile.Application.Common.Errors;
using Stockpile.Contracts.Authentication;
using Stockpile.Contracts.Errors;
using Stockpile.Domain.Entities.Identity;

namespace Stockpile.Application.Registration;

/// <summary>
/// Draft/resume for staged registration (business-flows §2 "each step is
/// independently idempotent and resumable via the draftToken", §3 "seven stages,
/// one resumable draft"; api.md §2.1). The use case owns the token lifecycle:
/// mint on the first save, address by hash afterwards, retire on completion.
/// <para>
/// It never sees a password. A draft is prefill text for a visitor who has not
/// registered yet, so nothing it stores grants anything and nothing it stores is
/// trusted: every field is trimmed and capped on the way in, and the flow it
/// belongs to is fixed at mint time (a buyer token can never resume a supplier
/// draft).
/// </para>
/// </summary>
public sealed class RegistrationDraftService
{
    /// <summary>A draft is a convenience, not a submission: caps keep an anonymous write bounded.</summary>
    private const int MaxCategories = 20;

    private const int MaxCategorySlugLength = 100;

    private readonly IRegistrationDraftStore _store;
    private readonly AuthOptions _options;
    private readonly TimeProvider _time;

    public RegistrationDraftService(IRegistrationDraftStore store, IOptions<AuthOptions> options, TimeProvider time)
    {
        _store = store;
        _options = options.Value;
        _time = time;
    }

    /// <summary>
    /// Creates a draft (no/blank token) or replaces the prefill of a live one
    /// (valid token), answering the stored state either way — so the client that
    /// autosaved can treat the response as its new state.
    /// </summary>
    public async Task<RegistrationDraftState> SaveAsync(
        SaveRegistrationDraftRequest request, CancellationToken cancellationToken)
    {
        string kind = NormalizeKind(request.Kind);
        RegistrationDraftPrefill prefill = NormalizePrefill(request);
        DateTime nowUtc = _time.GetUtcNow().UtcDateTime;
        string? token = Blank(request.DraftToken) ? null : request.DraftToken!.Trim();

        if (token is null)
        {
            string minted = OpaqueTokens.Create();
            var draft = new RegistrationDraft
            {
                Id = Guid.NewGuid(),
                TokenHash = OpaqueTokens.Hash(minted),
                Kind = kind,
                Email = prefill.Email,
                FirstName = prefill.FirstName,
                LastName = prefill.LastName,
                Phone = prefill.Phone,
                CompanyName = prefill.CompanyName,
                RegistrationNumber = prefill.RegistrationNumber,
                Website = prefill.Website,
                CreatedAtUtc = nowUtc,
                UpdatedAtUtc = nowUtc,
                ExpiresAtUtc = nowUtc.AddHours(_options.RegistrationDraftExpiryHours),
                Categories = prefill.Categories
                    .Select(slug => new RegistrationDraftCategory { Slug = slug })
                    .ToList(),
            };

            // Opportunistic sweep: expired drafts are already unreachable
            // (FindLiveByHashAsync filters on expiry); this is what eventually
            // removes them, so an anonymous flow cannot grow rows forever.
            await _store.DeleteExpiredAsync(nowUtc, cancellationToken);
            await _store.CreateAsync(draft, cancellationToken);

            return Present(minted, draft);
        }

        RegistrationDraft live = await FindLiveAsync(token, kind, nowUtc, cancellationToken);

        live.Email = prefill.Email;
        live.FirstName = prefill.FirstName;
        live.LastName = prefill.LastName;
        live.Phone = prefill.Phone;
        live.CompanyName = prefill.CompanyName;
        live.RegistrationNumber = prefill.RegistrationNumber;
        live.Website = prefill.Website;
        live.UpdatedAtUtc = nowUtc;

        await _store.UpdateAsync(live, prefill.Categories, cancellationToken);

        return Present(token, live);
    }

    /// <summary>Resume: the draft's stored prefill, for the holder of its token.</summary>
    public async Task<RegistrationDraftState> ResumeAsync(
        ResumeRegistrationDraftRequest request, CancellationToken cancellationToken)
    {
        if (Blank(request.DraftToken))
        {
            throw new AppException(
                ErrorCodes.ValidationFailed,
                [new FieldError("draftToken", "Required", "A draft token is required to resume.")]);
        }

        string token = request.DraftToken!.Trim();
        RegistrationDraft live = await FindLiveAsync(token, kind: null, _time.GetUtcNow().UtcDateTime, cancellationToken);
        return Present(token, live);
    }

    /// <summary>
    /// Retires the draft a completed registration came from — the single-use end of
    /// the token's life. A dead token is simply not an error here: registration's
    /// job is to create the account, and the caller (which owns the API layer's
    /// failure policy) decides what a store fault means once the account exists.
    /// </summary>
    public Task RetireAsync(string? draftToken, Guid userId, CancellationToken cancellationToken)
        => Blank(draftToken)
            ? Task.CompletedTask
            : _store.CompleteAsync(
                OpaqueTokens.Hash(draftToken!.Trim()), userId, _time.GetUtcNow().UtcDateTime, cancellationToken);

    /// <summary>
    /// The live draft behind a token, or one identical 404 — unknown, expired,
    /// retired and wrong-flow tokens are indistinguishable (the token grants
    /// nothing, so there is nothing to tell apart).
    /// </summary>
    private async Task<RegistrationDraft> FindLiveAsync(
        string token, string? kind, DateTime nowUtc, CancellationToken cancellationToken)
    {
        RegistrationDraft? live = await _store.FindLiveByHashAsync(OpaqueTokens.Hash(token), nowUtc, cancellationToken);

        if (live is null || (kind is not null && !string.Equals(live.Kind, kind, StringComparison.Ordinal)))
        {
            throw new AppException(ErrorCodes.ResourceNotFound);
        }

        return live;
    }

    private static RegistrationDraftState Present(string token, RegistrationDraft draft)
        => new(
            DraftToken: token,
            Kind: draft.Kind,
            ExpiresAtUtc: draft.ExpiresAtUtc,
            Prefill: new RegistrationDraftPrefill(
                Email: draft.Email,
                FirstName: draft.FirstName,
                LastName: draft.LastName,
                Phone: draft.Phone,
                CompanyName: draft.CompanyName,
                RegistrationNumber: draft.RegistrationNumber,
                Website: draft.Website,
                Categories: draft.Categories
                    .Select(row => row.Slug)
                    .OrderBy(slug => slug, StringComparer.Ordinal)
                    .ToList()));

    private static string NormalizeKind(string? kind)
    {
        string normalized = kind?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!RegistrationDraftKinds.IsKnown(normalized))
        {
            throw new AppException(
                ErrorCodes.ValidationFailed,
                [new FieldError("kind", "Unknown", "A draft belongs to the buyer or the supplier registration flow.")]);
        }

        return normalized;
    }

    private static RegistrationDraftPrefill NormalizePrefill(SaveRegistrationDraftRequest request)
    {
        List<string> categories = (request.Categories ?? [])
            .Select(slug => slug?.Trim() ?? string.Empty)
            .Where(slug => slug.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (categories.Count > MaxCategories)
        {
            throw new AppException(
                ErrorCodes.ValidationFailed,
                [new FieldError("categories", "TooMany", $"Choose at most {MaxCategories} categories.")]);
        }

        if (categories.Any(slug => slug.Length > MaxCategorySlugLength))
        {
            throw new AppException(
                ErrorCodes.ValidationFailed,
                [new FieldError("categories", "TooLong", "A category slug is too long.")]);
        }

        return new RegistrationDraftPrefill(
            Email: Field(request.Email, 320, "email"),
            FirstName: Field(request.FirstName, 100, "firstName"),
            LastName: Field(request.LastName, 100, "lastName"),
            Phone: Field(request.Phone, 32, "phone"),
            CompanyName: Field(request.CompanyName, 200, "companyName"),
            RegistrationNumber: Field(request.RegistrationNumber, 64, "registrationNumber"),
            Website: Field(request.Website, 300, "website"),
            Categories: categories);
    }

    /// <summary>Trim, treat whitespace as absent, and refuse a value past the column's bound.</summary>
    private static string? Field(string? value, int maxLength, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim();
        if (trimmed.Length > maxLength)
        {
            throw new AppException(
                ErrorCodes.ValidationFailed,
                [new FieldError(field, "TooLong", $"This value may not exceed {maxLength} characters.")]);
        }

        return trimmed;
    }

    private static bool Blank(string? value) => string.IsNullOrWhiteSpace(value);
}
