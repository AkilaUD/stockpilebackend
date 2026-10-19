using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Stockpile.Application.Audit;
using Stockpile.Application.Common.Errors;
using Stockpile.Application.Organizations;
using Stockpile.Application.Suppliers;
using Stockpile.Contracts.Authentication;
using Stockpile.Contracts.Errors;
using Stockpile.Domain.Common;
using Stockpile.Domain.Entities.Identity;
using Stockpile.Domain.Enums;

namespace Stockpile.Application.Authentication;

/// <summary>
/// Task 1.36 — the authentication use cases (security-test-matrix A1–A15,
/// threat-model §13.4). Registration creates the staged buyer account (step 1
/// of business-flows §2), login verifies credentials against the
/// exponential-backoff lockout and creates a revocable <see cref="LoginSession"/>
/// row, and logout / logout-all / session-revoke operate on those server-side
/// rows — a session stops working the moment it is revoked, not at the next
/// ticket renewal. Transport stays out: the service returns outcomes and a
/// ClaimsPrincipal; the Api layer signs the cookie and mints antiforgery
/// tokens (§1.10). Failure paths throw <see cref="AppException"/> with a code
/// the exception handler maps — never a crafted response from this layer.
/// </summary>
public sealed class AuthenticationService
{
    private readonly UserManager<User> _userManager;
    private readonly IRoleStore<Role> _roleStore;
    private readonly ILoginSessionStore _sessions;
    private readonly IRefreshSessionStore _refreshSessions;
    private readonly IPasswordResetTokenStore _resetTokens;
    private readonly IEmailVerificationTokenStore _verificationTokens;
    private readonly ICurrentRequestContext _requestContext;
    private readonly IUserPermissionStore _permissions;
    private readonly IOrganizationStore _organizations;
    private readonly ISupplierRegistrationStore _supplierRegistration;
    private readonly IAuditService _audit;
    private readonly ISecurityEventService _securityEvents;
    private readonly IPasswordHasher<User> _passwordHasher;
    private readonly IServiceProvider _services;
    private readonly TimeProvider _time;
    private readonly AuthOptions _options;
    private readonly int _lockoutThreshold;

    public AuthenticationService(
        UserManager<User> userManager,
        IRoleStore<Role> roleStore,
        ILoginSessionStore sessions,
        IRefreshSessionStore refreshSessions,
        IPasswordResetTokenStore resetTokens,
        IEmailVerificationTokenStore verificationTokens,
        ICurrentRequestContext requestContext,
        IUserPermissionStore permissions,
        IOrganizationStore organizations,
        ISupplierRegistrationStore supplierRegistration,
        IAuditService audit,
        ISecurityEventService securityEvents,
        IPasswordHasher<User> passwordHasher,
        IServiceProvider services,
        TimeProvider time,
        IOptions<AuthOptions> options,
        IOptions<IdentityOptions> identityOptions)
    {
        _userManager = userManager;
        _roleStore = roleStore;
        _sessions = sessions;
        _refreshSessions = refreshSessions;
        _resetTokens = resetTokens;
        _verificationTokens = verificationTokens;
        _requestContext = requestContext;
        _permissions = permissions;
        _organizations = organizations;
        _supplierRegistration = supplierRegistration;
        _audit = audit;
        _securityEvents = securityEvents;
        _passwordHasher = passwordHasher;
        _services = services;
        _time = time;
        _options = options.Value;
        // Task 1.39 — the trigger threshold read from Identity's own options,
        // so the backoff bump cannot drift from where the counter is compared.
        _lockoutThreshold = identityOptions.Value.Lockout.MaxFailedAccessAttempts;
        _dummyHash = new Lazy<string>(() =>
            _passwordHasher.HashPassword(
                new User { Email = "dummy-no-such-account" }, $"dummy-{Guid.NewGuid():N}"));
    }

    /// <summary>
    /// Successful sign-in outcome (login AND refresh — task 1.40 shares the
    /// shape): the principal to sign into the session cookie, the session row
    /// id, the identity payload, and the opaque refresh token plus its row
    /// expiry that the Api layer hands to the second HttpOnly cookie.
    /// </summary>
    public sealed record LoginSucceeded(
        ClaimsPrincipal Principal,
        Guid SessionId,
        AuthSessionResponse Identity,
        string RefreshToken,
        DateTime RefreshExpiresAt);

    // ---- Registration (business-flows §2 step 1) ---------------------------

    /// <summary>
    /// Creates an active buyer account using the email as its login identifier.
    /// Email ownership is not verified during registration.
    /// </summary>
    public async Task<Guid> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken)
    {
        Role registeredRole = await ResolveRoleAsync(
            _options.RegisteredUserRole, "Auth:RegisteredUserRole", cancellationToken);

        User user = await CreateAccountAsync(
            request.Email,
            request.Password,
            registeredRole.Code,
            firstName: request.Email.Trim().Split('@', 2)[0],
            lastName: string.Empty,
            phone: null,
            cancellationToken);

        await _audit.RecordAsync(
            new AuditEntry("auth.register", "User", user.PublicId.ToString(), AuditResult.Success), cancellationToken);

        // The caller retires this registration's draft (if any) — see
        // AuthController.RetireDraftAsync for why that is done outside this use case.
        return user.PublicId;
    }

    /// <summary>
    /// Task 2F — supplier self-registration (business-flows §3.3, api.md §2.1
    /// <c>POST /auth/register/supplier</c>): one submit creates the account with
    /// the configured supplier role and then — in one transaction — the supplier
    /// organisation, the caller's accepted membership, the supplier aggregate
    /// with its public profile and its category coverage. Category slugs are
    /// resolved against the live taxonomy BEFORE the account exists, so a submit
    /// that names no real category fails without creating a user.
    /// </summary>
    public async Task<Guid> RegisterSupplierAsync(RegisterSupplierRequest request, CancellationToken cancellationToken)
    {
        Role supplierRole = await ResolveRoleAsync(
            _options.RegisteredSupplierRole, "Auth:RegisteredSupplierRole", cancellationToken);

        IReadOnlyList<Guid> categoryIds =
            await _supplierRegistration.ResolveCategoryIdsAsync(request.Categories, cancellationToken);
        if (categoryIds.Count == 0)
        {
            throw new AppException(
                ErrorCodes.ValidationFailed,
                [new FieldError("categories", "Unknown", "Choose at least one category the platform recognises.")]);
        }

        string firstName = request.ContactFirstName.Trim();
        string lastName = request.ContactLastName.Trim();

        User user = await CreateAccountAsync(
            request.Email,
            request.Password,
            supplierRole.Code,
            firstName,
            lastName,
            request.Phone.Trim(),
            cancellationToken);

        Guid supplierId = await _supplierRegistration.CreateSupplierAsync(
            new SupplierRegistrationDraft(
                UserId: user.PublicId,
                CompanyName: request.CompanyName,
                RegistrationNumber: request.RegistrationNumber,
                Website: request.Website,
                ContactPerson: $"{firstName} {lastName}".Trim(),
                Phone: request.Phone.Trim(),
                Email: request.Email.Trim(),
                CategoryIds: categoryIds),
            cancellationToken);

        await _audit.RecordAsync(
            new AuditEntry("supplier.register", "Supplier", supplierId.ToString(), AuditResult.Success), cancellationToken);

        return user.PublicId;
    }

    // ---- Shared registration helpers ---------------------------------------

    /// <summary>
    /// The one account-creation path both registrations share: normalise the
    /// login, refuse a duplicate, run the breach check when enabled, create the
    /// Identity user and assign the single seeded role. Buyer and supplier
    /// registration differ only in the role code and the person fields.
    /// </summary>
    private async Task<User> CreateAccountAsync(
        string email,
        string password,
        string roleCode,
        string firstName,
        string lastName,
        string? phone,
        CancellationToken cancellationToken)
    {
        string normalizedEmail = NormalizeEmail(email);

        if (await _userManager.FindByEmailAsync(normalizedEmail) is not null)
        {
            throw new AppException(
                ErrorCodes.ValidationFailed,
                [new FieldError("email", "Duplicate", "An account with this email already exists.")]);
        }

        if (await IsBreachCheckingEnabledAsync())
        {
            IPasswordBreachChecker checker = _services.GetRequiredService<IPasswordBreachChecker>();
            if (await checker.IsBreachedAsync(password, cancellationToken))
            {
                throw new AppException(
                    ErrorCodes.ValidationFailed,
                    [new FieldError("password", "Breached", "This password appears in known data breaches. Choose a different one.")]);
            }
        }

        var user = new User
        {
            PublicId = Guid.NewGuid(),
            Email = email.Trim(),
            EmailNormalized = normalizedEmail,
            EmailConfirmedAt = _time.GetUtcNow().UtcDateTime,
            FirstName = firstName,
            LastName = lastName,
            Phone = phone,
            PreferredLocale = "en",
            Status = UserStatus.Active,
        };

        IdentityResult created = await _userManager.CreateAsync(user, password);
        if (!created.Succeeded)
        {
            throw ToValidationFailure(created);
        }

        IdentityResult roleAssigned = await _userManager.AddToRoleAsync(user, roleCode);
        if (!roleAssigned.Succeeded)
        {
            throw ToValidationFailure(roleAssigned);
        }

        return user;
    }

    /// <summary>
    /// Resolve a config-seeded role code, failing fast BEFORE an account is
    /// created: a misconfigured role must not produce a role-less account that
    /// the seeder would later contradict (domain-model §2.2).
    /// </summary>
    private async Task<Role> ResolveRoleAsync(string roleName, string settingName, CancellationToken cancellationToken)
    {
        Role? role = await _roleStore.FindByNameAsync(roleName.ToUpperInvariant(), cancellationToken);
        if (role is null)
        {
            throw new InvalidOperationException(
                $"{settingName} '{roleName}' is not seeded in 'Seed:Roles'. "
                + "Roles are seeded from configuration (domain-model §2.2); register a role with that code.");
        }

        return role;
    }

    // ---- Login (threat-model §13.4, T1.1) ----------------------------------

    /// <summary>
    /// Verifies credentials and — on success — creates the server-side session
    /// and returns a principal for the cookie. Non-zero outcomes:
    /// <list type="bullet">
    ///   <item>unknown email / suspended-deleted account / wrong password → 401
    ///   <c>INVALID_CREDENTIALS</c> with an identical response AND time profile
    ///   (A3 — no enumeration; a dummy hash verification equalizes the
    ///   password-check cost on the paths that skip it);</item>
    ///   <item>account locked (A1) → 403 <c>ACCOUNT_LOCKED</c> naming the wait;</item>
    ///   <item>unverified email signs in fine — the A11 gate is on RFQ submit.</item>
    /// </list>
    /// Every failure and every success writes its <c>SecurityEvent</c>; a
    /// threshold of failures triggers the lockout (<c>IdentityOptions.Lockout</c>)
    /// whose window is the task 1.39 exponential backoff — base × 2^(episode−1)
    /// minutes with ±20 % jitter, capped at <c>Auth:LockoutMaxMinutes</c>, reset
    /// by the owner's success, a completed password reset, or an admin unlock.
    /// </summary>
    public async Task<LoginSucceeded> LoginAsync(
        LoginRequest request, string? ipAddress, string? userAgent, CancellationToken cancellationToken)
    {
        User? user = await _userManager.FindByEmailAsync(NormalizeEmail(request.Email));

        if (user is null || user.Status != UserStatus.Active)
        {
            // Same shape AND same cost as a wrong password (A3): unknown or
            // disabled accounts cannot be distinguished by timing.
            await VerifyDummyPasswordAsync(request.Password);
            throw new AppException(ErrorCodes.InvalidCredentials);
        }

        DateTimeOffset? lockoutEnd = await _userManager.GetLockoutEndDateAsync(user);
        DateTimeOffset now = _time.GetUtcNow();
        if (lockoutEnd is { } lockedUntil && lockedUntil > now)
        {
            int minutes = Math.Max(1, (int)Math.Ceiling((lockedUntil - now).TotalMinutes));
            throw new AppException(
                ErrorCodes.AccountLocked,
                $"This account is locked after repeated failed sign-ins. Try again in about {minutes} minute{(minutes == 1 ? string.Empty : "s")}.");
        }

        if (!await _userManager.CheckPasswordAsync(user, request.Password))
        {
            // Task 1.39 — the trigger is detected by the COUNTER, captured BEFORE
            // AccessFailedAsync: .NET 10 resets FailedAccessCount to zero the
            // moment the lockout triggers (verified — each episode earns a
            // fresh threshold), so the post-call count would read 0 exactly on
            // the crossing failure. Entry while already locked throws above, so
            // before == threshold − 1 here means THIS failure crossed the line
            // → the next episode's window.
            int failuresBefore = await _userManager.GetAccessFailedCountAsync(user);
            await _userManager.AccessFailedAsync(user);
            await _securityEvents.RecordAsync(
                SecurityEventType.LoginFailure, user.PublicId, "sign-in rejected: wrong password", cancellationToken: cancellationToken);

            if (failuresBefore >= _lockoutThreshold - 1)
            {
                // Task 1.39 — the exponential backoff with jitter replaces the
                // fixed window: the 5th failure's Identity-flagged end is
                // overridden with base × 2^(episode−1) minutes, ±20% (defeats
                // scripted retry timing), capped at Auth:LockoutMaxMinutes —
                // repeated lockouts grow the ladder, never permanently (the cap
                // is the "alert rather than permanent lock" of threat-model
                // §13.4 line 168).
                user.LockoutEpisode++;
                TimeSpan window = LockoutBackoff.Window(
                    user.LockoutEpisode,
                    _options.LockoutBaseMinutes,
                    _options.LockoutMaxMinutes,
                    LockoutBackoff.RandomJitterMultiplier());
                user.LockoutEndsAt = now.UtcDateTime + window;
                IdentityResult locked = await _userManager.UpdateAsync(user);
                if (!locked.Succeeded)
                {
                    throw ToValidationFailure(locked);
                }

                await _securityEvents.RecordAsync(
                    SecurityEventType.LockoutTriggered,
                    user.PublicId,
                    $"account locked after repeated failed sign-ins; episode {user.LockoutEpisode}, window ≈ {Math.Ceiling(window.TotalMinutes):F0} min",
                    cancellationToken: cancellationToken);
            }

            throw new AppException(ErrorCodes.InvalidCredentials);
        }

        if (user.FailedAccessCount > 0 || user.LockoutEndsAt is not null)
        {
            if (user.LockoutEndsAt is not null)
            {
                await _securityEvents.RecordAsync(
                    SecurityEventType.LockoutReleased, user.PublicId, "lock released on successful sign-in", cancellationToken: cancellationToken);
            }

            // The victim's own success re-arms the backoff ladder at its base
            // (an attacker's failures can never reset it — only success can).
            user.LockoutEpisode = 0;
            await _userManager.ResetAccessFailedCountAsync(user);
            await _userManager.SetLockoutEndDateAsync(user, null);
            IdentityResult flushed = await _userManager.UpdateAsync(user);
            if (!flushed.Succeeded)
            {
                throw ToValidationFailure(flushed);
            }
        }

        LoginSession session = await _sessions.CreateAsync(
            user.PublicId, TimeSpan.FromHours(_options.AbsoluteSessionHours), ipAddress, userAgent, cancellationToken);

        // Task 1.40 — the second HttpOnly cookie's credential: an opaque
        // 256-bit refresh token whose SHA-256 hash is the only thing stored
        // (architecture §8.1 "rotating refresh token in a second HttpOnly
        // cookie", threat-model T1.4).
        (string refreshToken, DateTime refreshExpiresAt) = await IssueRefreshTokenAsync(
            session, _time.GetUtcNow().UtcDateTime, cancellationToken);

        await _audit.RecordAsync(new AuditEntry(
            "auth.login", "User", user.PublicId.ToString(), AuditResult.Success), cancellationToken);
        await _securityEvents.RecordAsync(
            SecurityEventType.LoginSuccess, user.PublicId, "sign-in succeeded", cancellationToken: cancellationToken);

        IReadOnlyList<string> roles = await RolesOfAsync(user, cancellationToken);
        IReadOnlyList<string> permissionCodes =
            await _permissions.GetPermissionCodesAsync(user.PublicId, cancellationToken);
        IReadOnlyList<OrganizationMembershipResponse> memberships =
            await MembershipsOfAsync(user.PublicId, cancellationToken);

        return new LoginSucceeded(
            BuildPrincipal(user, session.Id, roles),
            session.Id,
            BuildIdentity(user, roles, permissionCodes, memberships),
            refreshToken,
            refreshExpiresAt);
    }

    // ---- Refresh rotation (task 1.40, threat-model T1.4, A8/A9) -------------

    /// <summary>
    /// Rotates the presented refresh token and re-establishes the session.
    /// EVERY dead state answers one identical 401 <c>SESSION_EXPIRED</c> —
    /// unknown, expired, revoked (A8), already-rotated, or a session that is
    /// itself dead — because the reason a token is dead is never revealed.
    /// Presenting a token that is no longer the live head of its chain is
    /// REUSE (A9): every refresh row of the session AND the
    /// <see cref="LoginSession"/> are revoked, and <c>RefreshReuseDetected</c>
    /// raises the alarm. A clean rotation advances the chain — old row →
    /// <c>RotatedToId</c> → successor — so the OLD token can never become live
    /// again (the row, never a stateless blob, is the authority), and the
    /// successor's expiry is capped at the session's absolute end: a refresh
    /// token can never outlive the session it re-establishes.
    /// </summary>
    public async Task<LoginSucceeded> RefreshSessionAsync(
        string? refreshToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            throw new AppException(ErrorCodes.SessionExpired);
        }

        RefreshSession? row = await _refreshSessions.FindByHashAsync(HashToken(refreshToken), cancellationToken);
        if (row is null)
        {
            throw new AppException(ErrorCodes.SessionExpired);
        }

        LoginSession? session = await _sessions.FindAsync(row.SessionId, cancellationToken);
        DateTime nowUtc = _time.GetUtcNow().UtcDateTime;
        if (session is null)
        {
            throw new AppException(ErrorCodes.SessionExpired);
        }

        // A9 — a rotated head presented again is theft evidence: the chain and
        // its session die together, because no holder can be told apart.
        if (row.RotatedToId is not null)
        {
            await ReuseDetectedAsync(session, nowUtc, cancellationToken);
            throw new AppException(ErrorCodes.SessionExpired);
        }

        // A8 — every other dead state (row revoked or expired, session revoked
        // or past its 12 h absolute cap) answers the same 401; stamp the rows
        // still marked live so the ledger matches reality — the chain ends here.
        if (row.RevokedAt is not null || row.ExpiresAt <= nowUtc
            || session.RevokedAt is not null || session.ExpiresAt <= nowUtc)
        {
            await _refreshSessions.RevokeChainAsync(session.Id, nowUtc, cancellationToken);
            throw new AppException(ErrorCodes.SessionExpired);
        }

        User? user = await _userManager.FindByIdAsync(session.UserId.ToString());
        if (user is null || user.Status != UserStatus.Active)
        {
            // The session outlived its account (deleted or suspended between
            // login and refresh): behave as anonymous, chain included.
            await _refreshSessions.RevokeChainAsync(session.Id, nowUtc, cancellationToken);
            throw new AppException(ErrorCodes.SessionExpired);
        }

        string rotatedToken = CreateOpaqueToken();
        var successor = new RefreshSession
        {
            Id = Guid.NewGuid(),
            SessionId = session.Id,
            TokenHash = HashToken(rotatedToken),
            ExpiresAt = RefreshExpiry(nowUtc, session),
        };

        RefreshSession? advanced = await _refreshSessions.RotateAsync(row.Id, successor, cancellationToken);
        if (advanced is null)
        {
            // A concurrent replay won the rotation race — this caller is the
            // reuse: revoke everything and alarm (A9).
            await ReuseDetectedAsync(session, nowUtc, cancellationToken);
            throw new AppException(ErrorCodes.SessionExpired);
        }

        IReadOnlyList<string> roles = await RolesOfAsync(user, cancellationToken);
        IReadOnlyList<string> permissionCodes =
            await _permissions.GetPermissionCodesAsync(user.PublicId, cancellationToken);
        IReadOnlyList<OrganizationMembershipResponse> memberships =
            await MembershipsOfAsync(user.PublicId, cancellationToken);

        await _audit.RecordAsync(new AuditEntry(
            "auth.refresh", "LoginSession", session.Id.ToString(), AuditResult.Success), cancellationToken);

        return new LoginSucceeded(
            BuildPrincipal(user, session.Id, roles),
            session.Id,
            BuildIdentity(user, roles, permissionCodes, memberships),
            rotatedToken,
            successor.ExpiresAt);
    }

    /// <summary>
    /// Reuse response (T1.4): revoke EVERY refresh row of the session plus the
    /// <see cref="LoginSession"/> itself, write the <c>RefreshReuseDetected</c>
    /// security event (the alert trail), and audit the denial — ONCE. A repeat
    /// replay of the already-dead chain (a stuck client, or an attacker
    /// grinding a revoked cookie) finds nothing left to stamp and answers its
    /// 401 in silence, so a dead credential can never flood the alarm trail.
    /// </summary>
    private async Task ReuseDetectedAsync(LoginSession session, DateTime nowUtc, CancellationToken cancellationToken)
    {
        int stamped = await _refreshSessions.RevokeChainAsync(session.Id, nowUtc, cancellationToken);
        bool sessionWasLive = session.RevokedAt is null;
        if (sessionWasLive)
        {
            await _sessions.RevokeAsync(session.Id, nowUtc, cancellationToken);
        }

        if (stamped == 0 && !sessionWasLive)
        {
            return;
        }

        await _securityEvents.RecordAsync(
            SecurityEventType.RefreshReuseDetected,
            session.UserId,
            "refresh token reuse detected; session chain revoked",
            cancellationToken: cancellationToken);
        await _audit.RecordAsync(new AuditEntry(
            "auth.refresh.reuse", "LoginSession", session.Id.ToString(), AuditResult.Failure), cancellationToken);
    }

    /// <summary>
    /// Mints the opaque 256-bit refresh token and persists its hashed row;
    /// returns the raw token (cookie-only) and the row expiry the cookie's
    /// Expires attribute mirrors.
    /// </summary>
    private async Task<(string Token, DateTime ExpiresAt)> IssueRefreshTokenAsync(
        LoginSession session, DateTime nowUtc, CancellationToken cancellationToken)
    {
        string token = CreateOpaqueToken();
        var row = new RefreshSession
        {
            Id = Guid.NewGuid(),
            SessionId = session.Id,
            TokenHash = HashToken(token),
            ExpiresAt = RefreshExpiry(nowUtc, session),
        };
        await _refreshSessions.CreateAsync(row, cancellationToken);
        return (token, row.ExpiresAt);
    }

    /// <summary>now + Auth:RefreshTokenMinutes, never past the session's absolute end.</summary>
    private DateTime RefreshExpiry(DateTime nowUtc, LoginSession session)
    {
        DateTime expiry = nowUtc.AddMinutes(_options.RefreshTokenMinutes);
        return session.ExpiresAt < expiry ? session.ExpiresAt : expiry;
    }

    /// <summary>256 bits from the CSPRNG — the raw value only ever travels in the cookie, only the hash is stored.</summary>
    private static string CreateOpaqueToken()
        => OpaqueTokens.Create();

    // ---- Logout / session management ---------------------------------------

    /// <summary>Revokes one session (idempotent — a repeated logout is still 204).</summary>
    public async Task LogoutAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        LoginSession? session = await _sessions.FindAsync(sessionId, cancellationToken);
        if (session is null || session.RevokedAt is not null)
        {
            return;
        }

        DateTime nowUtc = _time.GetUtcNow().UtcDateTime;
        await _sessions.RevokeAsync(sessionId, nowUtc, cancellationToken);
        // Task 1.40 — the refresh chain dies with its session (A8): the
        // second cookie must not outlive the ticket it re-establishes.
        await _refreshSessions.RevokeChainAsync(sessionId, nowUtc, cancellationToken);
        await _securityEvents.RecordAsync(
            SecurityEventType.SessionRevoked, session.UserId, "session ended by user logout", cancellationToken: cancellationToken);
        await _audit.RecordAsync(new AuditEntry(
            "auth.logout", "LoginSession", sessionId.ToString(), AuditResult.Success), cancellationToken);
    }

    /// <summary>Revokes every live session of the user — including the current one (A15, §13.4).</summary>
    public async Task LogoutAllAsync(Guid userId, CancellationToken cancellationToken)
    {
        DateTime nowUtc = _time.GetUtcNow().UtcDateTime;
        int revoked = await _sessions.RevokeAllAsync(userId, nowUtc, cancellationToken);
        if (revoked == 0)
        {
            return;
        }

        // Task 1.40 — every refresh chain of the user dies with its session
        // (A8): presenting one afterwards answers 401, chain already revoked.
        await _refreshSessions.RevokeAllForUserAsync(userId, nowUtc, cancellationToken);
        await _securityEvents.RecordAsync(
            SecurityEventType.AllSessionsRevoked,
            userId,
            $"sign-out on all devices; {revoked} session(s) revoked",
            cancellationToken: cancellationToken);
        await _audit.RecordAsync(new AuditEntry(
            "auth.logout-all", "User", userId.ToString(), AuditResult.Success), cancellationToken);
    }

    /// <summary>Live sessions for the directory: current first, then newest activity. Item shapes come from Stockpile.Contracts (the wire source of truth).</summary>
    public async Task<IReadOnlyList<ActiveSessionResponse>> ListSessionsAsync(
        Guid userId, Guid currentSessionId, CancellationToken cancellationToken)
    {
        IReadOnlyList<LoginSession> rows =
            await _sessions.ListActiveAsync(userId, _time.GetUtcNow().UtcDateTime, cancellationToken);

        return rows
            .OrderByDescending(session => session.Id == currentSessionId)
            .ThenByDescending(session => session.LastSeenAt)
            .ThenByDescending(session => session.IssuedAt)
            .Select(session => new ActiveSessionResponse(
                session.Id.ToString(),
                session.Id == currentSessionId,
                session.IssuedAt,
                session.ExpiresAt,
                session.LastSeenAt,
                session.IpAddress,
                session.UserAgent))
            .ToList();
    }

    /// <summary>
    /// Revokes one session from the directory. Returns false when the session
    /// is unknown or belongs to another user — the caller answers 404
    /// <c>RESOURCE_NOT_FOUND</c> (api.md §1.6 BOLA: existence is never leaked
    /// across users).
    /// </summary>
    public async Task<bool> RevokeSessionAsync(
        Guid userId, Guid sessionId, CancellationToken cancellationToken)
    {
        LoginSession? session = await _sessions.FindAsync(sessionId, cancellationToken);
        if (session is null || session.UserId != userId)
        {
            return false;
        }

        if (session.RevokedAt is null)
        {
            DateTime nowUtc = _time.GetUtcNow().UtcDateTime;
            await _sessions.RevokeAsync(sessionId, nowUtc, cancellationToken);
            // Task 1.40 — revoking a session revokes its refresh chain too (A8).
            await _refreshSessions.RevokeChainAsync(sessionId, nowUtc, cancellationToken);
            await _securityEvents.RecordAsync(
                SecurityEventType.SessionRevoked, userId, "session revoked from the session list", cancellationToken: cancellationToken);
            await _audit.RecordAsync(new AuditEntry(
                "auth.session.revoke", "LoginSession", sessionId.ToString(), AuditResult.Success), cancellationToken);
        }

        return true;
    }

    // ---- Password reset & change (threat-model T1.5, security-test-matrix A4–A6) ----

    /// <summary>
    /// Step 1 of the password reset (business-flows §2): ALWAYS returns a
    /// token-shaped answer, for known AND unknown accounts alike (A4 — no
    /// enumeration). A known account gets a real opaque 256-bit token whose
    /// SHA-256 hash is stored — "hashed at rest", so a database breach yields
    /// nothing usable (threat-model T1.5); an unknown account gets a
    /// high-entropy inert token that validates against nothing. The two are
    /// indistinguishable by shape, format or cost. The same token rides BOTH
    /// the 202 response (the synchronous next step) and the
    /// <c>identity.password.reset_requested</c> outbox payload for the
    /// transactional reset email (1.38).
    /// </summary>
    public async Task<string> RequestPasswordResetAsync(string email, CancellationToken cancellationToken)
    {
        User? user = await _userManager.FindByEmailAsync(NormalizeEmail(email));

        if (user is null)
        {
            return Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        }

        byte[] rawToken = RandomNumberGenerator.GetBytes(32);
        string token = Convert.ToBase64String(rawToken);
        DateTime nowUtc = _time.GetUtcNow().UtcDateTime;

        // Issuing a new token consumes outstanding ones — exactly one live
        // token per user (OWASP "invalidate old tokens on re-issue").
        var reset = new PasswordResetToken
        {
            Id = Guid.NewGuid(),
            UserId = user.PublicId,
            TokenHash = HashToken(token),
            CreatedAtUtc = nowUtc,
            ExpiresAtUtc = nowUtc.AddMinutes(_options.PasswordResetTokenMinutes),
        };

        // 1.38 — the reset email is queued in the same SaveChanges that stores
        // the row: the outbox payload carries the opaque token for the renderer
        // (the outbox is delivery transport, never a log sink — threat-model
        // T3.6 — and the row is the single-use ledger, never the email).
        reset.Raise(new DomainEvent(
            nowUtc,
            user.PublicId,
            _requestContext.CorrelationId,
            "identity.password.reset_requested",
            JsonSerializer.Serialize(new { email = user.Email, token, expiresAtUtc = reset.ExpiresAtUtc }, PayloadJson)));
        await _resetTokens.CreateAsync(reset, cancellationToken);

        await _securityEvents.RecordAsync(
            SecurityEventType.PasswordResetRequested, user.PublicId, "password reset requested", cancellationToken: cancellationToken);
        await _audit.RecordAsync(new AuditEntry(
            "auth.password.reset-request", "User", user.PublicId.ToString(), AuditResult.Success), cancellationToken);
        return token;
    }

    /// <summary>
    /// Step 2 of the password reset. One identical 400 <c>INVALID_RESET_TOKEN</c>
    /// for an unknown account, an unknown token, an expired token (A6) or a
    /// re-used token (A5) — the reason is never revealed (non-enumeration);
    /// only the token holder learns their own token is dead. On success the
    /// stored row is consumed (single-use, A5), every session is revoked, and
    /// the account lock clears (reset is the locked account's recovery path;
    /// 1.39 builds on it).
    /// </summary>
    public async Task CompletePasswordResetAsync(
        string email, string token, string newPassword, CancellationToken cancellationToken)
    {
        User? user = await _userManager.FindByEmailAsync(NormalizeEmail(email));

        if (user is null)
        {
            // Equalise the PBKDF2 cost of the real reset path: hashing the new
            // password costs exactly what a real completion costs, so this
            // endpoint cannot be a timing oracle either (A4).
            _passwordHasher.HashPassword(
                new User { Email = "dummy-no-such-account" }, newPassword);
            throw new AppException(ErrorCodes.ResetTokenInvalid);
        }

        DateTime nowUtc = _time.GetUtcNow().UtcDateTime;
        PasswordResetToken? reset = await _resetTokens.FindPendingAsync(
            user.PublicId, HashToken(token), nowUtc, cancellationToken);
        if (reset is null)
        {
            throw new AppException(ErrorCodes.ResetTokenInvalid);
        }

        // Validate the new password BEFORE consuming the token, so a policy or
        // breach rejection never burns it — the user retries with a compliant
        // password.
        foreach (IPasswordValidator<User> validator in _userManager.PasswordValidators)
        {
            IdentityResult check = await validator.ValidateAsync(_userManager, user, newPassword);
            if (!check.Succeeded)
            {
                throw new AppException(ErrorCodes.ValidationFailed, PasswordFieldErrors(check));
            }
        }

        if (await IsBreachCheckingEnabledAsync())
        {
            IPasswordBreachChecker checker = _services.GetRequiredService<IPasswordBreachChecker>();
            if (await checker.IsBreachedAsync(newPassword, cancellationToken))
            {
                throw new AppException(
                    ErrorCodes.ValidationFailed,
                    [new FieldError("newPassword", "Breached", "This password appears in known data breaches. Choose a different one.")]);
            }
        }

        // Set the new password directly. (.NET 10's RemovePasswordAsync +
        // AddPasswordAsync pair is unusable here: Remove does not clear the
        // stored hash, so Add answers UserAlreadyHasPassword.) The Identity
        // policy validators ran above, so no rule is skipped. The domain model
        // has no security-stamp column (StockpileUserStore omits
        // IUserSecurityStampStore): single-use and revocation are carried by
        // the consumed token row and the session rows, not by a stamp.
        user.PasswordHash = _passwordHasher.HashPassword(user, newPassword);
        IdentityResult updated = await _userManager.UpdateAsync(user);
        if (!updated.Succeeded)
        {
            throw ToValidationFailure(updated);
        }

        // Single-use (A5): a 0 here means a concurrent completion consumed the
        // row first — the loser answers the same 400 as any dead token.
        int consumed = await _resetTokens.ConsumeAsync(reset.Id, nowUtc, cancellationToken);
        if (consumed == 0)
        {
            throw new AppException(ErrorCodes.ResetTokenInvalid);
        }

        // A5: completion revokes EVERY session — one device's forgotten
        // password must not leave the other devices holding live tickets.
        int revoked = await _sessions.RevokeAllAsync(user.PublicId, nowUtc, cancellationToken);
        // Task 1.40 — and every refresh chain behind those sessions (A8).
        await _refreshSessions.RevokeAllForUserAsync(user.PublicId, nowUtc, cancellationToken);

        // The reset is the account-recovery path: an account locked by bad
        // sign-ins unlocks on a completed reset (the lockout's counters would
        // otherwise trap the user until the window passed) and the backoff
        // ladder re-arms at its base.
        user.LockoutEpisode = 0;
        await _userManager.ResetAccessFailedCountAsync(user);
        await _userManager.SetLockoutEndDateAsync(user, null);

        await _audit.RecordAsync(new AuditEntry(
            "auth.password.reset", "User", user.PublicId.ToString(), AuditResult.Success), cancellationToken);
        await _securityEvents.RecordAsync(
            SecurityEventType.PasswordResetCompleted, user.PublicId, "password reset completed", cancellationToken: cancellationToken);
        if (revoked > 0)
        {
            await _securityEvents.RecordAsync(
                SecurityEventType.AllSessionsRevoked,
                user.PublicId,
                $"password reset; {revoked} session(s) revoked",
                cancellationToken: cancellationToken);
        }
    }

    /// <summary>
    /// Self-service password change (api.md §2.1): requires the current
    /// password (a mismatch is a 422 field error on <c>currentPassword</c>).
    /// THIS device stays signed in; every other session dies — a stolen stale
    /// ticket must not outlive the credential change (a PasswordChanged +
    /// AllSessionsRevoked event pair lands in the audit trail).
    /// </summary>
    public async Task ChangePasswordAsync(
        Guid userId, Guid? currentSessionId, string currentPassword, string newPassword, CancellationToken cancellationToken)
    {
        User user = await _userManager.FindByIdAsync(userId.ToString())
            ?? throw new AppException(ErrorCodes.Unauthenticated); // session outlived its account

        IdentityResult changed = await _userManager.ChangePasswordAsync(user, currentPassword, newPassword);
        if (!changed.Succeeded)
        {
            // A wrong current password must not leak anything beyond the field
            // it belongs to. Identity's own code carries the mapping.
            IReadOnlyList<FieldError> fields = changed.Errors
                .Select(error => new FieldError(
                    error.Code == "PasswordMismatch" ? "currentPassword" : "newPassword",
                    WireCode(error.Code),
                    error.Description))
                .ToList();
            throw new AppException(ErrorCodes.ValidationFailed, fields);
        }

        DateTime nowUtc = _time.GetUtcNow().UtcDateTime;
        int revoked = currentSessionId is { } keep
            ? await _sessions.RevokeOthersAsync(user.PublicId, keep, nowUtc, cancellationToken)
            : await _sessions.RevokeAllAsync(user.PublicId, nowUtc, cancellationToken);
        // Task 1.40 — the refresh chains follow their sessions exactly (A8):
        // this device keeps its token, every other chain dies with its ticket.
        if (currentSessionId is { } keptSession)
        {
            await _refreshSessions.RevokeOthersAsync(user.PublicId, keptSession, nowUtc, cancellationToken);
        }
        else
        {
            await _refreshSessions.RevokeAllForUserAsync(user.PublicId, nowUtc, cancellationToken);
        }

        await _audit.RecordAsync(new AuditEntry(
            "auth.password.change", "User", user.PublicId.ToString(), AuditResult.Success), cancellationToken);
        await _securityEvents.RecordAsync(
            SecurityEventType.PasswordChanged, user.PublicId, "password changed; other sessions revoked", cancellationToken: cancellationToken);
        if (revoked > 0)
        {
            await _securityEvents.RecordAsync(
                SecurityEventType.AllSessionsRevoked,
                user.PublicId,
                $"password changed; {revoked} other session(s) revoked",
                cancellationToken: cancellationToken);
        }
    }

    // ---- Email verification (threat-model line 169, security-test-matrix A11) ----

    /// <summary>
    /// Consumes the one-time verification token and confirms the address. The
    /// endpoint is anonymous: the token ITSELF identifies the account (it is
    /// found by hash alone), so an unknown, used or expired token answers one
    /// identical 400 <c>INVALID_VERIFICATION_TOKEN</c> — the reason is never
    /// revealed, matching the reset flow's A5/A6 discipline. An account that is
    /// already verified returns <see cref="EmailVerificationOutcome.AlreadyVerified"/>
    /// (defensive idempotency: a survivor row is consumed, the clicker never
    /// fails). Verification does NOT sign anyone in and does NOT revoke any
    /// session — the A11 gate opens on the RFQ submit path, and sign-in was
    /// never gated (domain-model "verification before RFQ submit, not before
    /// browsing or sign-in").
    /// </summary>
    public async Task<EmailVerificationOutcome> VerifyEmailAsync(
        string token, CancellationToken cancellationToken)
    {
        DateTime nowUtc = _time.GetUtcNow().UtcDateTime;
        EmailVerificationToken? row = await _verificationTokens.FindPendingByHashAsync(
            HashToken(token), nowUtc, cancellationToken);
        if (row is null)
        {
            throw new AppException(ErrorCodes.VerificationTokenInvalid);
        }

        User user = await _userManager.FindByIdAsync(row.UserId.ToString())
            ?? throw new AppException(ErrorCodes.VerificationTokenInvalid); // an orphaned row is as dead as any other dead token

        if (user.EmailConfirmedAt is not null)
        {
            // A crash between the update and the consume steps can leave the row
            // live after verification. Never fail the clicker: consume the
            // survivor and confirm the state.
            await _verificationTokens.ConsumeAsync(row.Id, nowUtc, cancellationToken);
            return EmailVerificationOutcome.AlreadyVerified;
        }

        user.EmailConfirmedAt = nowUtc;
        IdentityResult updated = await _userManager.UpdateAsync(user);
        if (!updated.Succeeded)
        {
            throw ToValidationFailure(updated);
        }

        // Single-use: a 0 here means a concurrent presentation of the same token
        // won the row — the account is verified either way, so success stands.
        await _verificationTokens.ConsumeAsync(row.Id, nowUtc, cancellationToken);

        await _audit.RecordAsync(new AuditEntry(
            "auth.email.verify", "User", user.PublicId.ToString(), AuditResult.Success), cancellationToken);
        await _securityEvents.RecordAsync(
            SecurityEventType.EmailVerified, user.PublicId, "email address verified", cancellationToken: cancellationToken);
        return EmailVerificationOutcome.Verified;
    }

    /// <summary>
    /// Re-queues the verification email for the authenticated caller: mints a
    /// FRESH token — which consumes any outstanding one, so an old link dies the
    /// moment a new one is issued (OWASP "invalidate old tokens") — raises the
    /// <c>identity.user.registered</c> outbox event, and answers
    /// <see cref="EmailResendOutcome.Sent"/>. An already-verified account answers
    /// <see cref="EmailResendOutcome.AlreadyVerified"/> (nothing to resend). The
    /// only field in play is the caller's own state: no enumeration surface.
    /// </summary>
    public async Task<EmailResendOutcome> ResendVerificationAsync(
        Guid userId, CancellationToken cancellationToken)
    {
        User user = await _userManager.FindByIdAsync(userId.ToString())
            ?? throw new AppException(ErrorCodes.Unauthenticated); // ticket outlived its account

        if (user.EmailConfirmedAt is not null)
        {
            return EmailResendOutcome.AlreadyVerified;
        }

        await MintVerificationTokenAsync(user, cancellationToken);
        await _securityEvents.RecordAsync(
            SecurityEventType.EmailVerificationRequested,
            user.PublicId,
            "verification email resent",
            cancellationToken: cancellationToken);
        await _audit.RecordAsync(new AuditEntry(
            "auth.email.resend", "User", user.PublicId.ToString(), AuditResult.Success), cancellationToken);
        return EmailResendOutcome.Sent;
    }

    /// <summary>
    /// The A11 gate (threat-model line 169): email verification is required
    /// before RFQ submit — NOT before browsing or sign-in. Every use case
    /// covered by that rule calls this first; an unverified principal answers
    /// 403 <c>EMAIL_NOT_VERIFIED</c>. The RFQ submit case is its first real
    /// caller; the <c>/_test/verified</c> seam proves the wire behaviour today.
    /// </summary>
    public async Task EnsureEmailVerifiedAsync(Guid userId, CancellationToken cancellationToken)
    {
        User user = await _userManager.FindByIdAsync(userId.ToString())
            ?? throw new AppException(ErrorCodes.Unauthenticated); // ticket outlived its account

        if (user.EmailConfirmedAt is null)
        {
            throw new AppException(ErrorCodes.EmailNotVerified);
        }
    }

    /// <summary>
    /// Admin unlock (security-test-matrix F7, task 1.39): clears the lockout
    /// state — end, failed count AND the backoff episode, re-arming the ladder
    /// at its base — so the account signs in immediately. The bounded admin
    /// surface (POST /admin/users/{id}/unlock, systemAdministrator only) is the
    /// legitimate escape hatch alongside the owner's own recovery paths
    /// (successful sign-in after the window, completed password reset). The
    /// caller holds the authorization gate; this method is the data path.
    /// </summary>
    public async Task UnlockUserAsync(Guid publicId, CancellationToken cancellationToken)
    {
        User user = await _userManager.FindByIdAsync(publicId.ToString())
            ?? throw new AppException(ErrorCodes.ResourceNotFound); // admin context: unknown ids are 404 outright

        if (user.FailedAccessCount == 0 && user.LockoutEndsAt is null && user.LockoutEpisode == 0)
        {
            return; // not locked — idempotent no-op, still 204 to the caller
        }

        user.LockoutEndsAt = null;
        user.FailedAccessCount = 0;
        user.LockoutEpisode = 0;
        IdentityResult updated = await _userManager.UpdateAsync(user);
        if (!updated.Succeeded)
        {
            throw ToValidationFailure(updated);
        }

        await _securityEvents.RecordAsync(
            SecurityEventType.LockoutReleased, user.PublicId, "account unlocked by administrator", cancellationToken: cancellationToken);
        await _audit.RecordAsync(new AuditEntry(
            "admin.users.unlock", "User", user.PublicId.ToString(), AuditResult.Success), cancellationToken);
    }

    /// <summary>
    /// Mints a one-time verification token and queues its email in one
    /// transaction: a fresh row (persisted hashed), its <c>identity.user.registered</c>
    /// outbox event carrying the opaque token for the renderer. The caller owns
    /// the security event (registration writes it once, resend writes it per
    /// request) — this helper only mints.
    /// </summary>
    private async Task MintVerificationTokenAsync(User user, CancellationToken cancellationToken)
    {
        byte[] rawToken = RandomNumberGenerator.GetBytes(32);
        string token = Convert.ToBase64String(rawToken);
        DateTime nowUtc = _time.GetUtcNow().UtcDateTime;

        // One live token per account (OWASP "invalidate old tokens on re-issue"):
        // issuing consumes outstanding rows for the same user.
        var row = new EmailVerificationToken
        {
            Id = Guid.NewGuid(),
            UserId = user.PublicId,
            TokenHash = HashToken(token),
            CreatedAtUtc = nowUtc,
            ExpiresAtUtc = nowUtc.AddMinutes(_options.EmailVerificationTokenMinutes),
        };

        row.Raise(new DomainEvent(
            nowUtc,
            user.PublicId,
            _requestContext.CorrelationId,
            "identity.user.registered",
            JsonSerializer.Serialize(new { email = user.Email, token, expiresAtUtc = row.ExpiresAtUtc }, PayloadJson)));

        await _verificationTokens.CreateAsync(row, cancellationToken);
    }

    /// <summary>SHA-256 hex of the opaque reset token — the ONLY thing stored at rest (T1.5 "hashed at rest").</summary>
    private static string HashToken(string token)
        => OpaqueTokens.Hash(token);

    /// <summary>camelCase for outbox payloads (the email renderer and tests read the same shape).</summary>
    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web);

    /// <summary>Reset-completion password errors map to the <c>newPassword</c> field (the form's field).</summary>
    private static List<FieldError> PasswordFieldErrors(IdentityResult result)
        => result.Errors
            .Select(error => new FieldError("newPassword", WireCode(error.Code), error.Description))
            .ToList();

    // ---- Identity payload (GET /auth/session, api.md §2.1) -----------------

    /// <summary>The identity payload for the authenticated principal — profile, state, roles, resolved permissions, memberships.</summary>
    public async Task<AuthSessionResponse> GetIdentityAsync(Guid publicId, CancellationToken cancellationToken)
    {
        User? user = await _userManager.FindByIdAsync(publicId.ToString())
            ?? throw new AppException(ErrorCodes.Unauthenticated); // session outlived its account: behave as anonymous

        IReadOnlyList<string> roles = await RolesOfAsync(user, cancellationToken);
        IReadOnlyList<string> permissionCodes =
            await _permissions.GetPermissionCodesAsync(user.PublicId, cancellationToken);
        IReadOnlyList<OrganizationMembershipResponse> memberships =
            await MembershipsOfAsync(user.PublicId, cancellationToken);
        return BuildIdentity(user, roles, permissionCodes, memberships);
    }

    // ---- Internals ---------------------------------------------------------

    private async Task<IReadOnlyList<string>> RolesOfAsync(User user, CancellationToken cancellationToken)
        => (await _userManager.GetRolesAsync(user))
            .Order(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// The caller's accepted organisation memberships (task 1.43, api.md §2.1):
    /// Pending invitations are not memberships and never appear here — accept,
    /// and its token, lands with the invitation flow. Ordered by organisation
    /// name for a stable payload.
    /// </summary>
    private async Task<IReadOnlyList<OrganizationMembershipResponse>> MembershipsOfAsync(
        Guid userId, CancellationToken cancellationToken)
        => (await _organizations.GetMembershipsAsync(userId, cancellationToken))
            .Where(membership => membership.Status == InvitationStatus.Accepted)
            .Select(membership => new OrganizationMembershipResponse(
                membership.OrganizationId.ToString(),
                membership.OrganizationName,
                membership.RoleCode))
            .ToList();

    private static AuthSessionResponse BuildIdentity(
        User user,
        IReadOnlyList<string> roles,
        IReadOnlyList<string> permissionCodes,
        IReadOnlyList<OrganizationMembershipResponse> orgMemberships)
        => new(
            user.PublicId.ToString(),
            user.Email,
            user.FirstName,
            user.LastName,
            user.PreferredLocale,
            EmailConfirmed: user.EmailConfirmedAt is not null,
            user.Status.ToString(),
            roles,
            permissionCodes.Order(StringComparer.Ordinal).ToList(),
            OrgMemberships: orgMemberships);

    /// <summary>
    /// The ticket principal: Name + NameIdentifier for Authorization, "sub" for
    /// the ApiRequestContext actor + rate-limit partition, "sid" for the
    /// session-revocation hook, and the role claims the permission policy
    /// provider (1.42) reads. Built here — the Api layer only replays it into
    /// the cookie.
    /// </summary>
    private static ClaimsPrincipal BuildPrincipal(User user, Guid sessionId, IReadOnlyList<string> roles)
    {
        var identity = new ClaimsIdentity(
            AuthConstants.IdentityAuthenticationType, ClaimTypes.Name, ClaimTypes.Role);
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, user.PublicId.ToString()));
        identity.AddClaim(new Claim(ClaimTypes.Name, user.Email));
        identity.AddClaim(new Claim(AuthConstants.SubjectClaim, user.PublicId.ToString()));
        identity.AddClaim(new Claim(AuthConstants.SessionIdClaim, sessionId.ToString()));
        foreach (string role in roles)
        {
            identity.AddClaim(new Claim(ClaimTypes.Role, role));
        }

        return new ClaimsPrincipal(identity);
    }

    private async Task<bool> IsBreachCheckingEnabledAsync()
        => _services.GetRequiredService<IOptions<PasswordPolicyOptions>>().Value.BreachCheckEnabled;

    private static string NormalizeEmail(string email) => email.Trim().ToUpperInvariant();

    private static AppException ToValidationFailure(IdentityResult result)
    {
        IdentityError[] errors = result.Errors.ToArray();
        if (errors.Length == 0)
        {
            errors = [new IdentityError { Code = "Invalid", Description = "The value failed validation." }];
        }

        IReadOnlyList<FieldError> fields = errors
            .Select(error => new FieldError(FieldFor(error.Code), WireCode(error.Code), error.Description))
            .ToList();
        return new AppException(ErrorCodes.ValidationFailed, fields);
    }

    /// <summary>Identity error codes have no field; the registration form names the field they belong to.</summary>
    private static string FieldFor(string code)
    {
        if (code.StartsWith("Password", StringComparison.Ordinal))
        {
            return "password";
        }

        if (code is "DuplicateUserName" or "DuplicateEmail" or "InvalidEmail" or "DuplicateRoleName")
        {
            return "email";
        }

        return "request";
    }

    /// <summary>The wire code: Duplicate* maps to the stable "Duplicate" form; everything else passes through.</summary>
    private static string WireCode(string code)
        => code is "DuplicateUserName" or "DuplicateEmail" or "DuplicateRoleName" ? "Duplicate" : code;

    /// <summary>
    /// A pre-hashed dummy password for the non-existing-account paths (A3).
    /// Cached once (constructor): every unknown-account login pays a real PBKDF2
    /// verification — same cost profile as a wrong-password login — without
    /// ever revealing whether the account exists.
    /// </summary>
    private readonly Lazy<string> _dummyHash;

    private Task VerifyDummyPasswordAsync(string password)
    {
        _passwordHasher.VerifyHashedPassword(
            new User { Email = "dummy-no-such-account" }, _dummyHash.Value, password);
        return Task.CompletedTask;
    }
}