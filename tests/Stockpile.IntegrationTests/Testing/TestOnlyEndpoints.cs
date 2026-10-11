using System.Security.Claims;
using FluentValidation;
using Asp.Versioning;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Stockpile.Api.Authorization;
using Stockpile.Api.Idempotency;
using Stockpile.Api.RateLimiting;
using Stockpile.Application.Audit;
using Stockpile.Application.Authentication;
using Stockpile.Application.Common.Errors;
using Stockpile.Contracts.Errors;
using Stockpile.Domain.Enums;
using AuthenticationService = Stockpile.Application.Authentication.AuthenticationService;

namespace Stockpile.IntegrationTests.Testing;

/// <summary>
/// Endpoints that exist only to drive the API's failure paths over real HTTP
/// (tasks 1.18–1.21 verification). Never part of the shipped app: this
/// assembly is referenced only by tests.
/// </summary>
[ApiController]
[ApiVersionNeutral]
[Route("_test")]
// Actions must be instance methods for MVC controller activation; most of
// these deliberately never touch instance data (they only throw or echo).
#pragma warning disable CA1822
public sealed class TestOnlyController : ControllerBase
{
    private readonly IAuditService _audit;
    private readonly IAntiforgery _antiforgery;
    private readonly AuthenticationService _auth;

    /// <summary>DI: services under test that the test-only endpoints drive.</summary>
    public TestOnlyController(IAuditService audit, IAntiforgery antiforgery, AuthenticationService auth)
    {
        _audit = audit;
        _antiforgery = antiforgery;
        _auth = auth;
    }

    /// <summary>
    /// Antiforgery seam (task 1.35): stores the double-submit cookie and
    /// returns the request token the suite echoes as X-CSRF-TOKEN. Every test
    /// client fetches one before its first unsafe verb. Explicitly anonymous —
    /// the deny-by-default fallback (T6.1) applies to this controller too.
    /// </summary>
    [HttpGet("token")]
    [AllowAnonymous]
    public IActionResult Token()
    {
        AntiforgeryTokenSet tokens = _antiforgery.GetAndStoreTokens(HttpContext);
        return Ok(new { requestToken = tokens.RequestToken });
    }
    /// <summary>Throws unclassified → 500 INTERNAL_ERROR envelope.</summary>
    [HttpGet("unhandled")]
    [AllowAnonymous]
    public IActionResult Unhandled() =>
        throw new InvalidOperationException("Deliberate test failure — must not reach the wire.");

    /// <summary>Known business failure → the status registered for the code (409).</summary>
    [HttpGet("business")]
    [AllowAnonymous]
    public IActionResult Business() =>
        throw new AppException(ErrorCodes.QuoteAlreadyAccepted);

    /// <summary>Binds fine, fails validation → 422 with fieldErrors.</summary>
    [HttpPost("echo")]
    [AllowAnonymous]
    public IActionResult Echo([FromBody] TestOnlyRequest request) => Ok(request);

    /// <summary>
    /// Rate-limit target (task 1.26): the test.tight policy (2/min) drives the
    /// 429 + headers + envelope assertions. Registered only under Testing.
    /// </summary>
    [HttpGet("rate-limit")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.TestTight)]
    public IActionResult RateLimit() => Ok(new { allowed = true });

    /// <summary>
    /// Critical-write surrogate (task 1.27): exercises the full
    /// Idempotency-Key guard — missing → 400, same key+body → replay with
    /// <c>Idempotency-Replayed: true</c>, same key+different body → 409. The
    /// body may be any bytes (only its hash matters); the endpoint echoes a
    /// reference the way a future create operation would.
    /// </summary>
    [HttpPost("idempotent")]
    [AllowAnonymous]
    [Idempotent]
    public IActionResult Idempotent()
    {
        HttpContext.Items[IdempotencyFilter.ReferenceItemsKey] = "test-op-001";
        return Ok(new { id = "test-op-001" });
    }

    /// <summary>
    /// Audit-pipeline surrogate (task 1.29): records an <c>AuditLog</c> row
    /// through the scoped <see cref="IAuditService"/>, so tests can assert the
    /// persisted row and its ambient enrichment (IP, user-agent, correlation
    /// id) over real HTTP.
    /// </summary>
    [HttpPost("audit")]
    [AllowAnonymous]
    public async Task<IActionResult> Audit([FromBody] TestAuditRequest request)
    {
        await _audit.RecordAsync(new AuditEntry(
            request.Action, request.TargetType, request.TargetId, request.Result));
        return Ok(new { recorded = true });
    }

    /// <summary>
    /// Cookie-auth seam (task 1.34): issues a signed-in ticket for a fixed
    /// principal so tests can assert the A14 cookie flags on a real
    /// Set-Cookie response and replay the cookie on later requests.
    /// </summary>
    [HttpGet("signin")]
    [AllowAnonymous] // the seam CREATES the ticket; it can never demand one
    public async Task<IActionResult> SignIn()
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, TestOnlySubjects.Name),
            new Claim(ClaimTypes.NameIdentifier, TestOnlySubjects.Id),
        ], CookieAuthenticationDefaults.AuthenticationScheme);

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
        return Ok(new { signedIn = true });
    }

    /// <summary>Cookie-auth seam (task 1.34): removes the ticket and clears the cookie.</summary>
    [HttpPost("signout")]
    [AllowAnonymous] // signing OUT must work for a caller the fallback would otherwise deny
    public async Task<IActionResult> SignOutTicket()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Ok(new { signedOut = true });
    }

    /// <summary>Reports the principal observed by the pipeline (people or nobody).</summary>
    [HttpGet("whoami")]
    [AllowAnonymous] // the whole point is answering for anonymous callers too
    public IActionResult WhoAmI() =>
        Ok(new { authenticated = User.Identity?.IsAuthenticated == true, name = User.Identity?.Name });

    /// <summary>
    /// Requires a valid ticket: anonymous requests reach the cookie handler's
    /// OnRedirectToLogin and must come back as 401 + UNAUTHENTICATED JSON, not
    /// a redirect (the API contract; task 1.34).
    /// </summary>
    [HttpGet("protected")]
    [Authorize]
    public IActionResult Protected() => Ok(new { name = User.Identity?.Name });

    /// <summary>
    /// A11 gate seam (task 1.38): the email-verification gate the future RFQ
    /// submit action runs first. An unverified authenticated principal answers
    /// 403 <c>EMAIL_NOT_VERIFIED</c>; a verified one passes. Runs the same
    /// <see cref="AuthenticationService.EnsureEmailVerifiedAsync"/> a gated use
    /// case calls, so the wire behaviour (security-test-matrix A11) is proven
    /// over real HTTP today and the RFQ submit task reuses the gate unchanged.
    /// </summary>
    [HttpGet("verified")]
    [Authorize]
    [DisableRateLimiting] // user-scoped read of one's own verification state; X13-reviewed opt-out
    public async Task<IActionResult> Verified(CancellationToken cancellationToken)
    {
        await _auth.EnsureEmailVerifiedAsync(SubjectId(), cancellationToken);
        return Ok(new { verified = true });
    }

    /// <summary>
    /// Task 1.42 — deny-by-default proof for an UNKNOWN policy name: the
    /// <c>PermissionPolicyProvider</c> resolves any name that is neither a
    /// registered policy nor a <c>Contracts.Permissions</c> code to an
    /// unsatisfiable policy (threat-model T6.1). Hitting this deliberately
    /// misnamed gate must answer 401 anonymous / 403 signed in — the action's
    /// body must never be reached (F10's runtime twin).
    /// </summary>
    [HttpGet("unknown-policy")]
    [Authorize(Policy = "not.a.registered.permission")]
    public IActionResult UnknownPolicy() => Ok(new { reached = true });

    /// <summary>
    /// Task 1.44 — the MFA policy-hook seam: the REAL registered
    /// <c>mfa.required</c> policy evaluated over real HTTP (provider branch 1
    /// answers the registered policy as-is — neither rewritten into a
    /// permission requirement nor collapsed into deny — and
    /// MfaAuthorizationHandler gates on the caller's fresh
    /// <c>TwoFactorEnabled</c>, never the ticket). A signed-in account that
    /// has not verified a code answers 403 <c>INSUFFICIENT_CAPABILITY</c>;
    /// one that has passes; anonymous answers 401. The shipped app attaches
    /// this policy to no action in phase 1 — enforcement is the §12 rollout
    /// decision — so this seam is where the hook stays proven, not aspirational.
    /// </summary>
    [HttpGet("mfa-gate")]
    [Authorize(Policy = MfaRequirement.PolicyName)]
    public IActionResult MfaGate() => Ok(new { mfa = "satisfied" });

    private Guid SubjectId() =>
        Guid.Parse(
            User.FindFirstValue("sub")
            ?? throw new InvalidOperationException("No subject claim on the verified seam principal."));
}
#pragma warning restore CA1822

/// <summary>Body model for the validation and model-binding paths.</summary>
public sealed record TestOnlyRequest(string? Name);

/// <summary>
/// Fixed subject for the task 1.34 sign-in seam — deterministic across the
/// whole suite, so cookie assertions never depend on generated ids.
/// </summary>
public static class TestOnlySubjects
{
    public const string Id = "00000000-0000-0000-0000-000000000001";
    public const string Name = "test-user";
}

/// <summary>Body model for the audit surrogate endpoint (task 1.29).</summary>
public sealed record TestAuditRequest(string Action, string TargetType, string? TargetId, AuditResult Result);

/// <summary>Requires a name — drives the 422 branch of the action filter.</summary>
public sealed class TestOnlyRequestValidator : AbstractValidator<TestOnlyRequest>
{
    /// <summary>Initializes the single rule.</summary>
    public TestOnlyRequestValidator()
    {
        RuleFor(request => request.Name).NotEmpty();
    }
}
