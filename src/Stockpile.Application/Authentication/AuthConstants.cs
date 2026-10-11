namespace Stockpile.Application.Authentication;

/// <summary>
/// Task 1.36 — claim names shared by the auth service (writes them) and the
/// cookie validation hook and controllers (read them). The cookie ticket is
/// the <c>sub</c> + <c>sid</c> principal; everything about the session that
/// must be revocable lives server-side in <c>LoginSession</c> (domain-model
/// §2.5), never in the un-verifiable ticket.
/// </summary>
public static class AuthConstants
{
    /// <summary>The authenticated subject: <c>User.PublicId</c> (the rate limiter's "subjectId" partition key, api.md §1.8).</summary>
    public const string SubjectClaim = "sub";

    /// <summary>The server-side <c>LoginSession.Id</c> this ticket belongs to.</summary>
    public const string SessionIdClaim = "sid";

    /// <summary>
    /// AuthenticationType on the claim identity inside the cookie ticket. The
    /// ticket is replayed under the cookie scheme regardless; this non-empty
    /// value is what makes <c>User.Identity.IsAuthenticated</c> true. It is a
    /// stable internal marker, never shown on the wire or stored in the DB.
    /// </summary>
    public const string IdentityAuthenticationType = "stockpile.session";
}