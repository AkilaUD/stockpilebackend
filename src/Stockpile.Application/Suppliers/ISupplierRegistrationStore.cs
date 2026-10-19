namespace Stockpile.Application.Suppliers;

/// <summary>
/// Persistence port for supplier onboarding, implemented in Infrastructure over
/// <c>Organizations</c>, <c>OrganizationMembers</c>, <c>Suppliers</c>,
/// <c>SupplierProfiles</c> and <c>SupplierCategories</c> (domain-model
/// §2.3/§3.1/§3.3). The use case owns identity and orchestration; this port owns
/// the single transactional write that turns a validated draft into a
/// member-managed supplier.
/// </summary>
public interface ISupplierRegistrationStore
{
    /// <summary>
    /// Resolve the submitted category slugs to their catalogue <c>PublicId</c>s,
    /// dropping any slug the taxonomy does not know. Returns an empty list when
    /// none resolve, which the caller treats as a validation failure.
    /// </summary>
    Task<IReadOnlyList<Guid>> ResolveCategoryIdsAsync(
        IReadOnlyList<string> slugs, CancellationToken cancellationToken);

    /// <summary>
    /// Create the supplier organisation, the caller's accepted membership, the
    /// supplier aggregate with its public profile, and its category coverage in
    /// ONE transaction (an abandoned submit never leaves a half-built supplier).
    /// Returns the new supplier's <c>PublicId</c>.
    /// </summary>
    Task<Guid> CreateSupplierAsync(
        SupplierRegistrationDraft draft, CancellationToken cancellationToken);
}

/// <summary>
/// The validated inputs of a supplier onboarding write. The user has already
/// been created (and holds the supplier role) before this draft is assembled;
/// the store never touches identity.
/// </summary>
public sealed record SupplierRegistrationDraft(
    Guid UserId,
    string CompanyName,
    string? RegistrationNumber,
    string? Website,
    string ContactPerson,
    string Phone,
    string Email,
    IReadOnlyList<Guid> CategoryIds);
