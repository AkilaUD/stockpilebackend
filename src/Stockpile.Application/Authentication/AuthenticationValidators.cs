using FluentValidation;

namespace Stockpile.Application.Authentication;

/// <summary>
/// Registration field rules (api.md §2.1, domain-model §2.1 column lengths).
/// The password's minimum length is enforced by the Identity validators in the
/// use case (config-driven <c>PasswordPolicy</c>, threat-model §4) so one
/// configuration owns the rule; here the rule is only "a password must be
/// present". Phone is optional and, when given, must be an E.164 string —
/// matching nvarchar(30) and the "E.164-normalised" invariant.
/// </summary>
public sealed class RegisterRequestValidator : AbstractValidator<RegisterRequest>
{
    public RegisterRequestValidator()
    {
        RuleFor(request => request.FirstName)
            .NotEmpty().WithMessage("First name is required.")
            .MaximumLength(100);
        RuleFor(request => request.LastName)
            .NotEmpty().WithMessage("Last name is required.")
            .MaximumLength(100);
        RuleFor(request => request.Email)
            .NotEmpty().WithMessage("Email is required.")
            .EmailAddress().WithMessage("Enter a valid email address.")
            .MaximumLength(256);
        RuleFor(request => request.Phone)
            .Matches("^\\+[1-9]\\d{1,14}$")
            .When(request => !string.IsNullOrWhiteSpace(request.Phone))
            .WithMessage("Phone must be an E.164 number, e.g. +94771234567.")
            .MaximumLength(30);
        RuleFor(request => request.Password)
            .NotEmpty().WithMessage("Choose a password.");
    }
}

/// <summary>Login requires both identifiers; the account's state is judged in the use case, not here.</summary>
public sealed class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(request => request.Email)
            .NotEmpty().WithMessage("Email is required.")
            .EmailAddress().WithMessage("Enter a valid email address.");
        RuleFor(request => request.Password)
            .NotEmpty().WithMessage("Password is required.");
    }
}

/// <summary>Reset-request needs an email; existence is judged in the use case (A4 answers 202 either way).</summary>
public sealed class PasswordResetRequestValidator : AbstractValidator<PasswordResetRequest>
{
    public PasswordResetRequestValidator()
    {
        RuleFor(request => request.Email)
            .NotEmpty().WithMessage("Email is required.")
            .EmailAddress().WithMessage("Enter a valid email address.");
    }
}

/// <summary>
/// Reset completion: a present token and a present new password. The token's
/// validity (single-use, short TTL, ownership) is judged by the use case — a
/// single 400 <c>INVALID_RESET_TOKEN</c> regardless of why it failed (A5/A6);
/// the password's length rule is enforced by the Identity validators in the
/// use case (config-driven <c>PasswordPolicy</c>), like registration.
/// </summary>
public sealed class PasswordResetCompleteValidator : AbstractValidator<PasswordResetComplete>
{
    public PasswordResetCompleteValidator()
    {
        RuleFor(request => request.Email)
            .NotEmpty().WithMessage("Email is required.")
            .EmailAddress().WithMessage("Enter a valid email address.");
        RuleFor(request => request.Token)
            .NotEmpty().WithMessage("The reset token is required.");
        RuleFor(request => request.NewPassword)
            .NotEmpty().WithMessage("Choose a new password.");
    }
}

/// <summary>Password change needs both passwords; their relationship and rules are judged in the use case.</summary>
public sealed class PasswordChangeRequestValidator : AbstractValidator<PasswordChangeRequest>
{
    public PasswordChangeRequestValidator()
    {
        RuleFor(request => request.CurrentPassword)
            .NotEmpty().WithMessage("Enter your current password.");
        RuleFor(request => request.NewPassword)
            .NotEmpty().WithMessage("Choose a new password.");
    }
}

/// <summary>
/// Verification needs a present token; its validity (single-use, short TTL,
/// ownership) is judged by the use case — one 400 <c>INVALID_VERIFICATION_TOKEN</c>
/// regardless of why it failed, matching the reset flow's A5/A6 discipline.
/// </summary>
public sealed class EmailVerifyRequestValidator : AbstractValidator<EmailVerifyRequest>
{
    public EmailVerifyRequestValidator()
    {
        RuleFor(request => request.Token)
            .NotEmpty().WithMessage("The verification token is required.");
    }
}

/// <summary>
/// Task 1.44 — the code field's shape (api.md §2.1): exactly six digits at the
/// request filter, one wire code <c>invalid_code</c> for both a missing and a
/// malformed value so the client sees a single stable field code. Validity
/// against the stored secret is the use case's call (MfaService) — the filter
/// never decides MFA, only the shape.
/// </summary>
public sealed class MfaVerifyRequestValidator : AbstractValidator<MfaVerifyRequest>
{
    public MfaVerifyRequestValidator()
    {
        RuleFor(request => request.Code)
            .NotEmpty()
            .WithMessage("Enter the 6-digit code from your authenticator app.")
            .WithErrorCode("invalid_code")
            .Matches(@"^\d{6}$")
            .WithMessage("Enter the 6-digit code from your authenticator app.")
            .WithErrorCode("invalid_code");
    }
}