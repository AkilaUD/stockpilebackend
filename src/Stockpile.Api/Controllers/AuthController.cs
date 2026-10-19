using System.Security.Claims;
using Asp.Versioning;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Stockpile.Api.Configuration;
using Stockpile.Api.RateLimiting;
using Stockpile.Application.Authentication;
using Stockpile.Application.Common.Errors;
using Stockpile.Application.Registration;
using Stockpile.Application.Suppliers;
using Stockpile.Contracts.Authentication;
using Stockpile.Contracts.Errors;
// The framework also owns a static Microsoft.AspNetCore.Authentication.AuthenticationService;
// the alias pins this file to the use-case service.
using AuthenticationService = Stockpile.Application.Authentication.AuthenticationService;

namespace Stockpile.Api.Controllers;

/// <summary>
/// Task 1.36 — authentication & identity (api.md §2.1, security-test-matrix
/// A1–A15). Registration (staged buyer, step 1) and login mint antiforgery
/// tokens (§1.10); login additionally signs the session cookie. Logout,
/// logout-all and session revocation operate on the server-side
/// <see cref="Stockpile.Domain.Entities.Identity.LoginSession"/> rows, so a
/// revoked session stops working immediately (the cookie validation hook in
/// AuthenticationConfiguration enforces it on every request).
/// </summary>
[ApiController]
[ApiVersion(1.0)]
[Route("api/v{version:apiVersion}/auth")]
public sealed partial class AuthController : ControllerBase
{
    private readonly AuthenticationService _auth;
    private readonly MfaService _mfa;
    private readonly RegistrationDraftService _drafts;
    private readonly IAntiforgery _antiforgery;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AuthController> _logger;

    public AuthController(
        AuthenticationService auth,
        MfaService mfa,
        RegistrationDraftService drafts,
        IAntiforgery antiforgery,
        IConfiguration configuration,
        ILogger<AuthController> logger)
    {
        _auth = auth;
        _mfa = mfa;
        _drafts = drafts;
        _antiforgery = antiforgery;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Creates an active buyer account from its email login and password. Email
    /// ownership is not verified; the configured public-registration role is
    /// still the only role assigned. The response returns a request token so
    /// the SPA can make its next unsafe verb without a minting round-trip.
    /// </summary>
    [HttpPost("register")]
    // Explicit anonymous decision (threat-model T6.1 — deny by default, so
    // every public action marks itself; F10's build test enforces it).
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.AuthRegister)] // §13.7: 5 / hour in Production
    public async Task<IActionResult> Register([FromBody] RegisterRequest request, CancellationToken cancellationToken)
    {
        Guid userId = await _auth.RegisterAsync(request, cancellationToken);
        await RetireDraftAsync(request.DraftToken, userId, cancellationToken);

        AntiforgeryTokenSet tokens = _antiforgery.GetAndStoreTokens(HttpContext);
        string requestToken = tokens.RequestToken
            ?? throw new InvalidOperationException("Antiforgery minting on a fresh unsafe verb must produce a request token.");
        StampTokenResponse();
        return Created("/api/v1/auth/session",
            new RegisterResponse(Status: "registered", RequestToken: tokens.RequestToken));
    }

    /// <summary>
    /// Task 2F — supplier self-registration (business-flows §3.3, api.md §2.1
    /// <c>POST /auth/register/supplier</c>): anonymous and rate-limited exactly
    /// like buyer registration. One submit creates the account holding the
    /// supplier role and, in one transaction, the supplier company, the
    /// caller's accepted membership and the supplier's category coverage. Email
    /// ownership is not verified during signup (the A11 gate is on RFQ submit).
    /// </summary>
    [HttpPost("register/supplier")]
    [AllowAnonymous] // public registration — explicit, never implicit (T6.1)
    [EnableRateLimiting(RateLimitPolicies.AuthRegister)] // §13.7: 5 / hour
    public async Task<IActionResult> RegisterSupplier(
        [FromBody] RegisterSupplierRequest request, CancellationToken cancellationToken)
    {
        Guid userId = await _auth.RegisterSupplierAsync(request, cancellationToken);
        await RetireDraftAsync(request.DraftToken, userId, cancellationToken);

        AntiforgeryTokenSet tokens = _antiforgery.GetAndStoreTokens(HttpContext);
        string requestToken = tokens.RequestToken
            ?? throw new InvalidOperationException("Antiforgery minting on a fresh unsafe verb must produce a request token.");
        StampTokenResponse();
        return Created("/api/v1/auth/session",
            new RegisterResponse(Status: "registered", RequestToken: requestToken));
    }

    /// <summary>
    /// Retires the draft a successful signup came from (business-flows §2/§3:
    /// completing the registration consumes its resumable draft).
    /// <para>
    /// Best-effort by design. The account exists by the time this runs, so failing
    /// the response over a draft row would turn a successful signup into an error;
    /// the draft instead stays live (and still resumable) until it expires. A store
    /// fault is logged, never raised.
    /// </para>
    /// </summary>
    private async Task RetireDraftAsync(string? draftToken, Guid userId, CancellationToken cancellationToken)
    {
        if (draftToken is null)
        {
            return;
        }

        try
        {
            await _drafts.RetireAsync(draftToken, userId, cancellationToken);
        }
#pragma warning disable CA1031 // Deliberate: the account exists — a draft row must never fail a successful signup.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            LogDraftRetireFailed(_logger, exception);
        }
    }

    // Event ids 2300: registration-draft family (1900–1903 = error envelope, 2100–2102 = health,
    // 2200–2202 = rate limiting + antiforgery).
    [LoggerMessage(
        EventId = 2300,
        Level = LogLevel.Warning,
        Message = "Registration draft could not be retired after a successful signup.")]
    private static partial void LogDraftRetireFailed(ILogger logger, Exception exception);

    /// <summary>
    /// Signs in and issues all three cookies (session + refresh +
    /// antiforgery, §1.10 / architecture §8.1) with the minted request token
    /// in the body. 401 <c>INVALID_CREDENTIALS</c> is identical for unknown
    /// email, disabled account and wrong password (A3); a locked account
    /// answers 403 <c>ACCOUNT_LOCKED</c> (A1). The refresh cookie carries the
    /// opaque rotating token whose hashed row is the server-side authority
    /// (task 1.40).
    /// </summary>
    [HttpPost("login")]
    [AllowAnonymous] // public credential exchange — explicit, never implicit (T6.1)
    [EnableRateLimiting(RateLimitPolicies.AuthLogin)] // §13.7: 10 / 5 min, plus account lockout
    public async Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken cancellationToken)
    {
        var result = await _auth.LoginAsync(
            request,
            ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
            userAgent: HttpContext.Request.Headers.UserAgent.ToString(),
            cancellationToken);

        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, result.Principal);
        IssueRefreshCookie(result.RefreshToken, result.RefreshExpiresAt);
        // SignInAsync does not update HttpContext.User within the same request,
        // and an antiforgery token embeds the claim-Uid of the principal it was
        // minted under (ASVS-relevant: tokens never cross identities). Adopt the
        // freshly-issued principal BEFORE minting, so the token the SPA sends
        // next carries the identity its cookie will present (§1.10).
        HttpContext.User = result.Principal;
        AntiforgeryTokenSet tokens = _antiforgery.GetAndStoreTokens(HttpContext);
        string requestToken = tokens.RequestToken
            ?? throw new InvalidOperationException("Antiforgery minting on a fresh unsafe verb must produce a request token.");
        StampTokenResponse();

        return Ok(new LoginResponse(RequestToken: requestToken, User: result.Identity));
    }

    /// <summary>
    /// Task 1.40 — refresh rotation (threat-model T1.4, A8/A9): presents the
    /// <c>.Stockpile.Refresh</c> cookie, advances the chain (old row →
    /// <c>RotatedToId</c> → successor) and re-establishes the session — a fresh
    /// session cookie, a rotated refresh cookie, and a re-minted antiforgery
    /// token in a body shaped like login's, so the SPA treats it as a silent
    /// re-sign-in. Every dead state — unknown, expired, revoked by logout or
    /// logout-all (A8), already rotated — answers one identical 401
    /// <c>SESSION_EXPIRED</c>; presenting an old head revokes the WHOLE chain
    /// plus its session and raises <c>RefreshReuseDetected</c> (A9), once: a
    /// repeat replay of the dead cookie answers in silence (the exception path
    /// resets the response anyway — headers included — so the envelope stays
    /// the single failure shape). Anonymous by design: the refresh cookie IS
    /// the credential when the session ticket has lapsed. Global antiforgery
    /// still applies — a cookie-borne credential must never be CSRF-able
    /// (§1.10).
    /// </summary>
    [HttpPost("refresh")]
    [AllowAnonymous] // the refresh cookie IS the credential when the ticket lapsed (1.40)
    [EnableRateLimiting(RateLimitPolicies.AuthRefresh)] // §13.7: 60 / hour — stolen-token probing is a burst signal
    public async Task<IActionResult> Refresh(CancellationToken cancellationToken)
    {
        string? refreshToken = Request.Cookies[AuthenticationConfiguration.RefreshCookieName];
        AuthenticationService.LoginSucceeded result =
            await _auth.RefreshSessionAsync(refreshToken, cancellationToken);

        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, result.Principal);
        // Same identity adoption as login: the antiforgery token minted below
        // must embed the principal its cookie will present (§1.10).
        HttpContext.User = result.Principal;
        IssueRefreshCookie(result.RefreshToken, result.RefreshExpiresAt);
        AntiforgeryTokenSet tokens = _antiforgery.GetAndStoreTokens(HttpContext);
        string requestToken = tokens.RequestToken
            ?? throw new InvalidOperationException("Antiforgery minting on a fresh unsafe verb must produce a request token.");
        StampTokenResponse();

        return Ok(new RefreshResponse(RequestToken: requestToken, User: result.Identity));
    }

    /// <summary>
    /// The §1.10 minting endpoint: returns a fresh double-submit token for the
    /// CURRENT principal — anonymous when no ticket is presented — and stores
    /// the matching cookie. The SPA primes here before its first unsafe verb
    /// (registration) and re-primes after logout, whose 204 body is empty.
    /// Safe verb (GET), so never antiforgery-validated; minting is cheap,
    /// user-scoped and leaves no writable state (X13-style opt-out).
    /// </summary>
    [HttpGet("antiforgery")]
    [AllowAnonymous] // the priming endpoint itself must answer an anonymous first caller
    [DisableRateLimiting]
    public IActionResult Antiforgery()
    {
        AntiforgeryTokenSet tokens = _antiforgery.GetAndStoreTokens(HttpContext);
        string requestToken = tokens.RequestToken
            ?? throw new InvalidOperationException("Antiforgery minting must produce a request token.");
        StampTokenResponse();
        return Ok(new AntiforgeryTokenResponse(requestToken));
    }

    /// <summary>Current principal: profile, verification state, roles, resolved permissions, memberships (api.md §2.1).</summary>
    [HttpGet("session")]
    [Authorize]
    public Task<IActionResult> Session(CancellationToken cancellationToken)
        => SessionIdentityAsync(cancellationToken);

    /// <summary>The directory of this user's active sessions; the current one is marked (domain-model §2.5).</summary>
    [HttpGet("sessions")]
    [Authorize]
    public async Task<IActionResult> Sessions(CancellationToken cancellationToken)
    {
        IReadOnlyList<ActiveSessionResponse> items =
            await _auth.ListSessionsAsync(SubjectId(), CurrentSessionId()!.Value, cancellationToken);
        return Ok(new SessionsListResponse(items, CurrentSessionId()!.Value.ToString()));
    }

    /// <summary>Revokes the current session and clears the ticket (api.md §2.1; idempotent → 204).</summary>
    [HttpPost("logout")]
    [Authorize]
    // Authenticated, user-scoped, and non-sensitive — nothing to throttle against.
    // (login/register carry the credential-stuffing policies; X13 opt-out.)
    [DisableRateLimiting]
    public async Task<IActionResult> Logout(CancellationToken cancellationToken)
    {
        if (CurrentSessionId() is { } sessionId)
        {
            await _auth.LogoutAsync(sessionId, cancellationToken);
        }

        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        // Task 1.40 — the second cookie dies with the session it belongs to.
        ClearRefreshCookie();
        // The double-submit COOKIE is claims-free — only the request token carries
        // the identity claimUid — so nothing needs re-storing here. SignOutAsync
        // leaves HttpContext.User stale-authenticated though; a token minted now
        // would be bound to an identity that no longer exists. Pin it anonymous
        // so the cookie stays in sync with what the SPA's NEXT request presents.
        // The 204 body is empty by contract: the SPA re-primes (§1.10) via
        // GET /auth/antiforgery before its next anonymous write.
        HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity());
        AntiforgeryTokenSet tokens = _antiforgery.GetAndStoreTokens(HttpContext);
        _ = tokens; // cookie written; the value is read from the cookie by the SPA
        return NoContent();
    }

    /// <summary>Revokes EVERY session of the user — this device and every other (A15, §13.4).</summary>
    [HttpPost("logout-all")]
    [Authorize]
    [DisableRateLimiting] // same reasoning as logout: nothing to throttle against
    public async Task<IActionResult> LogoutAll(CancellationToken cancellationToken)
    {
        await _auth.LogoutAllAsync(SubjectId(), cancellationToken);
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        // Task 1.40 — logout-all clears the refresh cookie on THIS device too;
        // every other device's chain died server-side with its session (A8).
        ClearRefreshCookie();
        // Same anonymous re-pin as logout: nothing identity-bound may outlive
        // the ticket (the SPA re-primes before the next login).
        HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity());
        AntiforgeryTokenSet tokens = _antiforgery.GetAndStoreTokens(HttpContext);
        _ = tokens; // cookie written; the value is read from the cookie by the SPA
        return NoContent();
    }

    /// <summary>
    /// Revokes one session from the directory (api.md §2.1). A session that is
    /// unknown or belongs to another user answers 404 <c>RESOURCE_NOT_FOUND</c>
    /// — existence is never leaked across users (§1.6 BOLA). Revoking the
    /// current session also clears this ticket.
    /// </summary>
    [HttpDelete("sessions/{sessionId}")]
    [Authorize]
    [DisableRateLimiting] // GUID-v4 ids are not enumerable; each call is user-scoped
    public async Task<IActionResult> RevokeSession(Guid sessionId, CancellationToken cancellationToken)
    {
        if (!await _auth.RevokeSessionAsync(SubjectId(), sessionId, cancellationToken))
        {
            throw new AppException(ErrorCodes.ResourceNotFound);
        }

        if (sessionId == CurrentSessionId())
        {
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            ClearRefreshCookie(); // task 1.40 — revoking this device clears its second cookie
        }

        return NoContent();
    }

    /// <summary>
    /// Password reset step 1 (threat-model T1.5, A4): ALWAYS 202 regardless of
    /// whether the account exists, and an identical body either way — a known
    /// account receives a real reset token, an unknown one a high-entropy inert
    /// token. The same token rides this response and the
    /// <c>identity.password.reset_requested</c> outbox payload for the reset
    /// email (1.38). Minted responses are not cached and frame-guarded like every
    /// token channel (§1.10), and this request carries the antiforgery token of
    /// the anonymous principal.
    /// </summary>
    [HttpPost("password/reset-request")]
    [AllowAnonymous] // the token identifies the account; no session exists yet (A4)
    [EnableRateLimiting(RateLimitPolicies.AuthReset)] // §13.7: 3 / hour — the enumeration budget
    public async Task<IActionResult> PasswordResetRequest(
        [FromBody] PasswordResetRequest request, CancellationToken cancellationToken)
    {
        string token = await _auth.RequestPasswordResetAsync(request.Email, cancellationToken);
        StampTokenResponse();
        return Accepted(new PasswordResetRequestedResponse(Status: "reset_requested", RequestToken: token));
    }

    /// <summary>
    /// Password reset step 2 (A5/A6): the token is single-use (the security
    /// stamp rotates on success), completion revokes every session, and one
    /// identical 400 <c>INVALID_RESET_TOKEN</c> answers an invalid, expired,
    /// re-used or cross-account token — the reason is never revealed. Shares
    /// the <c>auth.reset</c> budget with the request step: one legitimately
    /// completes a reset within 3 attempts per hour.
    /// </summary>
    [HttpPost("password/reset")]
    [AllowAnonymous] // single-use token identifies the account; the caller has no session (A5/A6)
    [EnableRateLimiting(RateLimitPolicies.AuthReset)] // §13.7: same 3 / hour bucket as the request step
    public async Task<IActionResult> PasswordReset(
        [FromBody] PasswordResetComplete request, CancellationToken cancellationToken)
    {
        await _auth.CompletePasswordResetAsync(
            request.Email, request.Token, request.NewPassword, cancellationToken);
        return Ok(new PasswordResetCompleteResponse(Status: "password_reset"));
    }

    /// <summary>
    /// Self-service password change (api.md §2.1): requires the current
    /// password (a mismatch is a 422 field error), keeps THIS device signed in
    /// and revokes every other session. Credential-guessing surface is the same
    /// as login, so it shares the <c>auth.login</c> per-user budget.
    /// </summary>
    [HttpPost("password/change")]
    [Authorize]
    [EnableRateLimiting(RateLimitPolicies.AuthLogin)] // same abuse profile as login, same 10 / 5 min per-user budget
    public async Task<IActionResult> PasswordChange(
        [FromBody] PasswordChangeRequest request, CancellationToken cancellationToken)
    {
        await _auth.ChangePasswordAsync(
            SubjectId(), CurrentSessionId(), request.CurrentPassword, request.NewPassword, cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// Email verification (task 1.38, threat-model line 169, A11): consumes the
    /// one-time token from the verification email and confirms the address. The
    /// endpoint is anonymous — the token ITSELF identifies the account — and an
    /// unknown, used or expired token answers one identical 400
    /// <c>INVALID_VERIFICATION_TOKEN</c> (the reason is never revealed, the
    /// reset flow's A5/A6 discipline). An already-verified account answers 200
    /// <c>{status:"already_verified"}</c> (defensive idempotency, never a failed
    /// click). Verification does not sign anyone in; the A11 gate opens on the
    /// RFQ submit path.
    /// </summary>
    [HttpPost("email/verify")]
    [AllowAnonymous] // the one-time token ITSELF identifies the account (A11)
    [EnableRateLimiting(RateLimitPolicies.AuthVerify)] // §13.7: 10 / hour, shared with resend
    public async Task<IActionResult> VerifyEmail(
        [FromBody] EmailVerifyRequest request, CancellationToken cancellationToken)
    {
        EmailVerificationOutcome outcome = await _auth.VerifyEmailAsync(request.Token, cancellationToken);
        return Ok(new EmailVerifyResponse(
            outcome == EmailVerificationOutcome.AlreadyVerified ? "already_verified" : "verified"));
    }

    /// <summary>
    /// Re-queues the verification email (task 1.38, A11): mints a FRESH one-time
    /// token — the outstanding one dies, one live token per account — and answers
    /// 202 <c>{status:"verification_resent"}</c>. The new token travels in the
    /// <c>identity.user.registered</c> outbox payload for the email renderer, not
    /// the response. An already-verified account answers 200 <c>{status:"verified"}</c>.
    /// Shares the <c>auth.verify</c> budget with the verify step and is
    /// authenticated — resending is the account owner's own state, so there is no
    /// enumeration surface to rate-limit harder.
    /// </summary>
    [HttpPost("email/verify/resend")]
    [Authorize]
    [EnableRateLimiting(RateLimitPolicies.AuthVerify)] // §13.7: same 10 / hour bucket as the verify step
    public async Task<IActionResult> ResendVerification(CancellationToken cancellationToken)
    {
        EmailResendOutcome outcome = await _auth.ResendVerificationAsync(SubjectId(), cancellationToken);
        return outcome == EmailResendOutcome.AlreadyVerified
            ? Ok(new EmailVerifyResponse(Status: "verified"))
            : Accepted(new EmailVerifyResponse(Status: "verification_resent"));
    }

    /// <summary>
    /// MFA enrolment (task 1.44, threat-model §13.4 line 174 "UI opt-in"):
    /// mints a CSPRNG TOTP secret, stores the account PENDING and returns the
    /// secret with its otpauth URI for the authenticator app to scan — the ONE
    /// time the secret appears on the wire (never logged, never readable again;
    /// re-enrolling mints a fresh one and returns the account to pending, so a
    /// lost device is recoverable while no disable endpoint exists).
    /// <c>TwoFactorEnabled</c> flips true only in verify below — sign-in does
    /// not challenge yet (§12: architecture ready, enforcement is the rollout
    /// decision). The body carries credential material, so it must never be
    /// cached (§1.10 no-store discipline).
    /// </summary>
    [HttpPost("mfa/enroll")]
    [Authorize]
    [EnableRateLimiting(RateLimitPolicies.AuthMfa)] // §13.7: 10 / hour, shared with verify
    public async Task<IActionResult> EnrollMfa(CancellationToken cancellationToken)
    {
        MfaEnrollResponse response = await _mfa.EnrollAsync(SubjectId(), cancellationToken);
        StampTokenResponse();
        return Ok(response);
    }

    /// <summary>
    /// MFA verification (task 1.44): a 6-digit code from the enrolled
    /// authenticator app (RFC 6238 ±1 step, constant-time compare) is the ONLY
    /// writer of <c>TwoFactorEnabled</c> — 200 <c>{status:"enabled"}</c>, or the
    /// defensive-idempotent <c>{status:"already_enabled"}</c>. No pending
    /// enrolment answers 422 <c>code</c> <c>not_enrolled</c>; a wrong or
    /// malformed code 422 <c>code</c> <c>invalid_code</c> (shape enforced at the
    /// request filter AND re-checked in the use case — the filter never decides
    /// MFA).
    /// </summary>
    [HttpPost("mfa/verify")]
    [Authorize]
    [EnableRateLimiting(RateLimitPolicies.AuthMfa)] // §13.7: same 10 / hour bucket — the guessing budget
    public async Task<IActionResult> VerifyMfa(
        [FromBody] MfaVerifyRequest request, CancellationToken cancellationToken)
    {
        MfaVerifyResponse response = await _mfa.VerifyAsync(SubjectId(), request.Code, cancellationToken);
        return Ok(response);
    }

    private async Task<IActionResult> SessionIdentityAsync(CancellationToken cancellationToken)
    {
        AuthSessionResponse identity = await _auth.GetIdentityAsync(SubjectId(), cancellationToken);
        return Ok(identity);
    }

    /// <summary>The authenticated subject; authorized endpoints always carry it.</summary>
    private Guid SubjectId()
    {
        string subject = User.FindFirstValue(AuthConstants.SubjectClaim)
            ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new InvalidOperationException("An authenticated principal without an identity claim reached an authorized endpoint.");
        return Guid.Parse(subject);
    }

    private Guid? CurrentSessionId() =>
        Guid.TryParse(User.FindFirstValue(AuthConstants.SessionIdClaim), out Guid sessionId) ? sessionId : null;

    /// <summary>§1.10: token-issuing responses must not be cached and must frame-guard.</summary>
    private void StampTokenResponse()
    {
        Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        Response.Headers.XFrameOptions = "SAMEORIGIN";
    }

    /// <summary>
    /// Task 1.40 — the rotating refresh cookie (architecture §8.1): HttpOnly,
    /// SameSite=Lax, Secure in step with the session cookie (RequireHttps
    /// forces it on, otherwise SameAsRequest), persistent until the row it
    /// mirrors expires — the browser drops it exactly when the server does.
    /// </summary>
    private void IssueRefreshCookie(string token, DateTime expiresAtUtc)
    {
        Response.Cookies.Append(AuthenticationConfiguration.RefreshCookieName, token, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = IssuedSecure(),
            Path = "/",
            Expires = new DateTimeOffset(DateTime.SpecifyKind(expiresAtUtc, DateTimeKind.Utc)),
        });
    }

    /// <summary>Task 1.40 — expires the second cookie with matching attributes (path must match to target it).</summary>
    private void ClearRefreshCookie()
    {
        Response.Cookies.Delete(AuthenticationConfiguration.RefreshCookieName, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = IssuedSecure(),
            Path = "/",
        });
    }

    /// <summary>Secure exactly when the session cookie would be: RequireHttps forces it on, else SameAsRequest (§1.10/A14).</summary>
    private bool IssuedSecure()
        => _configuration.GetValue<bool>(AuthenticationConfiguration.RequireHttpsKey) || Request.IsHttps;
}