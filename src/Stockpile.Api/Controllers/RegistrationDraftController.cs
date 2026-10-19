using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Stockpile.Api.RateLimiting;
using Stockpile.Application.Registration;

namespace Stockpile.Api.Controllers;

/// <summary>
/// business-flows §2/§3 — the resumable registration draft (api.md §2.1). Both
/// actions are anonymous by design: the whole point of a draft is that the visitor
/// has no account yet. The token in the body is the only authority, and a dead or
/// foreign token answers one identical 404 (see <c>RegistrationDraftService</c>).
/// <para>
/// A successful save answers <c>200</c> rather than <c>201</c>: the operation is an
/// idempotent upsert of a transient prefill, and the draft has no canonical GET
/// URL to put in a <c>Location</c> header — the response body carries the state.
/// </para>
/// </summary>
[ApiController]
[ApiVersion(1.0)]
[Route("api/v{version:apiVersion}/auth/registration-drafts")]
public sealed class RegistrationDraftController : ControllerBase
{
    private readonly RegistrationDraftService _drafts;

    public RegistrationDraftController(RegistrationDraftService drafts) => _drafts = drafts;

    /// <summary>Creates a draft, or replaces the prefill of the one the token names.</summary>
    [HttpPost]
    [AllowAnonymous] // the visitor has no account yet — that is what a draft is for (T6.1)
    [EnableRateLimiting(RateLimitPolicies.AuthDraft)] // §13.7: bounded anonymous writes
    public async Task<IActionResult> Save(
        [FromBody] SaveRegistrationDraftRequest request, CancellationToken cancellationToken)
        => Ok(await _drafts.SaveAsync(request, cancellationToken));

    /// <summary>Hands back the stored prefill so an abandoned form can be picked up where it stopped.</summary>
    [HttpPost("resume")]
    [AllowAnonymous] // the token identifies its draft; the caller has no session (T6.1)
    [EnableRateLimiting(RateLimitPolicies.AuthDraft)] // §13.7: the same budget as saving
    public async Task<IActionResult> Resume(
        [FromBody] ResumeRegistrationDraftRequest request, CancellationToken cancellationToken)
        => Ok(await _drafts.ResumeAsync(request, cancellationToken));
}
