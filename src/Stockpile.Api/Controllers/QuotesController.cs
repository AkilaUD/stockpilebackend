using System.Security.Claims;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Stockpile.Api.Idempotency;
using Stockpile.Api.RateLimiting;
using Stockpile.Application.Authentication;
using Stockpile.Application.Procurement;
using Stockpile.Contracts.Permissions;
using Stockpile.Contracts.Procurement;

namespace Stockpile.Api.Controllers;

/// <summary>
/// Quotations (api.md §2.7, business-flows §7): the supplier answers one buying
/// lead with a quote whose totals the server computes. The lead is named by its
/// match id and resolved against the caller's own supplier, so a foreign lead is
/// one 404 and never inferable (BOLA, api.md §1.6). The write carries the §11.3
/// capability <c>quote.submit</c> — held by both supplier roles in the seeded
/// permission set — and the §1.8 budget <c>quote.submit</c> (60 / hour).
/// </summary>
[ApiController]
[ApiVersion(1.0)]
[Route("api/v{version:apiVersion}/quotes")]
public sealed class QuotesController(QuotesService quotes) : ControllerBase
{
    /// <summary>
    /// Creates the supplier's quote for one lead (api.md §2.7). The response
    /// carries the quote's identity plus the totals and coverage the server
    /// derived — no client-supplied figure is echoed as a total.
    /// <para>
    /// The <c>Idempotency-Key</c> header is required here (api.md §1.6): the call
    /// creates the quote record and moves the lead in one pass, so an honest retry
    /// replays the recorded response instead of answering
    /// <c>QUOTE_EXISTS_FOR_RFQ</c>.
    /// </para>
    /// </summary>
    [HttpPost]
    [Idempotent]
    [Authorize(Policy = Permissions.QuoteSubmit)]
    [EnableRateLimiting(RateLimitPolicies.QuoteSubmit)] // §1.8: 60 / hour per subject
    public async Task<ActionResult<QuoteResponse>> Submit(
        [FromBody] CreateQuoteRequest request, CancellationToken ct)
    {
        QuoteResponse quote = await quotes.SubmitAsync(SubjectId(), request, ct);
        HttpContext.Items[IdempotencyFilter.ReferenceItemsKey] = quote.PublicId;
        return Created($"/api/v1/quotes/{quote.PublicId}", quote);
    }

    /// <summary>The authenticated subject; [Authorize] guarantees it (1.41).</summary>
    private Guid SubjectId()
        => Guid.Parse(User.FindFirstValue(AuthConstants.SubjectClaim)
            ?? throw new InvalidOperationException(
                "Authorized action without a subject claim; the permission pipeline must have rejected the ticket."));
}
