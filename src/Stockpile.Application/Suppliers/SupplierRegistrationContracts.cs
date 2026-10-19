namespace Stockpile.Application.Suppliers;

/// <summary>
/// Supplier self-registration (business-flows §3.3, api.md §2.1
/// <c>POST /auth/register/supplier</c>). One atomic submit collects the
/// account, the person, the business and its category coverage; the supplier
/// lands <c>Active</c> with <c>VerificationState.Unverified</c>, exactly the
/// landing state the catalogue seeder assigns a vetted external supplier
/// (transactional verification documents, plan selection and the resumable
/// seven-stage draft are later stages and are deliberately not accepted here).
/// </summary>
public sealed record RegisterSupplierRequest(
    string Email,
    string Password,
    string ContactFirstName,
    string ContactLastName,
    string Phone,
    string CompanyName,
    string? RegistrationNumber,
    string? Website,
    IReadOnlyList<string> Categories,
    string? DraftToken = null);
