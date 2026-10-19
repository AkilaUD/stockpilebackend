using System.Security.Claims;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Stockpile.Api.Idempotency;
using Stockpile.Api.RateLimiting;
using Stockpile.Application.Authentication;
using Stockpile.Application.Orders;
using Stockpile.Application.Procurement;
using Stockpile.Contracts.Orders;
using Stockpile.Contracts.Permissions;
using Stockpile.Contracts.Procurement;

namespace Stockpile.Api.Controllers;

/// <summary>
/// Quotations (api.md §2.7, business-flows §7): the supplier answers one buying
/// lead with a quote whose totals the server computes, and the two parties then
/// move it through its lifecycle — revise, withdraw, decline, ask for a revision,
/// accept. The lead is named by its match id and resolved against the caller's
/// own supplier, and every quote id is resolved through the caller's own side
/// (supplier or buyer organisation), so a foreign quote is one 404 and never
/// inferable (BOLA, api.md §1.6). Each write carries its §11.3 capability and a
/// §1.8 budget, and — because a retry must not repeat the act — requires an
/// <c>Idempotency-Key</c>.
/// </summary>
[ApiController]
[ApiVersion(1.0)]
[Route("api/v{version:apiVersion}")]
public sealed class QuotesController(QuotesService quotes, OrderService orders) : ControllerBase
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
    [HttpPost("quotes")]
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

    /// <summary>
    /// One quote with its full revision history, as the supplier that wrote it or
    /// the buyer organisation that owns the RFQ (api.md §2.7). The viewer is the
    /// caller's side of the quote — a supplier never sees the buyer's identity,
    /// and no one else sees it at all.
    /// </summary>
    [HttpGet("quotes/{quoteId:guid}")]
    [Authorize]
    public async Task<ActionResult<QuoteDetailResponse>> Get(Guid quoteId, CancellationToken ct)
        => Ok(await quotes.GetAsync(SubjectId(), quoteId, ct));

    /// <summary>The supplier's own quotes, newest first (<c>quote.read.supplier</c>).</summary>
    [HttpGet("suppliers/me/quotes")]
    [Authorize(Policy = Permissions.QuoteReadSupplier)]
    public async Task<ActionResult<IReadOnlyList<QuoteSummaryResponse>>> ListMine(CancellationToken ct)
        => Ok(await quotes.ListForSupplierAsync(SubjectId(), ct));

    /// <summary>The quotes received on the caller's organisations' RFQs, newest first (<c>quote.read.buyer</c>).</summary>
    [HttpGet("me/quotes")]
    [Authorize(Policy = Permissions.QuoteReadBuyer)]
    public async Task<ActionResult<IReadOnlyList<QuoteSummaryResponse>>> ListReceived(CancellationToken ct)
        => Ok(await quotes.ListForBuyerAsync(SubjectId(), ct));

    /// <summary>
    /// Appends a revision (api.md §2.7). Revisions are append-only: the earlier
    /// figures stay as the buyer saw them, and the quote's current revision moves
    /// forward. Allowed only after the buyer has asked for a revision.
    /// </summary>
    [HttpPost("quotes/{quoteId:guid}/revisions")]
    [Idempotent]
    [Authorize(Policy = Permissions.QuoteRevise)]
    [EnableRateLimiting(RateLimitPolicies.QuoteRevision)] // §1.8: 20 / hour per subject
    public async Task<ActionResult<QuoteResponse>> Revise(
        Guid quoteId, [FromBody] ReviseQuoteRequest request, CancellationToken ct)
    {
        QuoteResponse quote = await quotes.ReviseAsync(SubjectId(), quoteId, request, ct);
        HttpContext.Items[IdempotencyFilter.ReferenceItemsKey] = quote.PublicId;
        return Created($"/api/v1/quotes/{quote.PublicId}", quote);
    }

    /// <summary>The supplier withdraws its own open quote (<c>quote.withdraw</c>).</summary>
    [HttpPost("quotes/{quoteId:guid}/withdraw")]
    [Idempotent]
    [Authorize(Policy = Permissions.QuoteWithdraw)]
    [EnableRateLimiting(RateLimitPolicies.QuoteRespond)]
    public async Task<ActionResult<QuoteSummaryResponse>> Withdraw(Guid quoteId, CancellationToken ct)
    {
        QuoteSummaryResponse quote = await quotes.WithdrawAsync(SubjectId(), quoteId, ct);
        HttpContext.Items[IdempotencyFilter.ReferenceItemsKey] = quote.PublicId;
        return Ok(quote);
    }

    /// <summary>The buyer declines a quote, with a reason (<c>quote.decline</c>).</summary>
    [HttpPost("quotes/{quoteId:guid}/decline")]
    [Idempotent]
    [Authorize(Policy = Permissions.QuoteDecline)]
    [EnableRateLimiting(RateLimitPolicies.QuoteRespond)]
    public async Task<ActionResult<QuoteSummaryResponse>> Decline(
        Guid quoteId, [FromBody] DeclineQuoteRequest request, CancellationToken ct)
    {
        QuoteSummaryResponse quote = await quotes.DeclineAsync(SubjectId(), quoteId, request, ct);
        HttpContext.Items[IdempotencyFilter.ReferenceItemsKey] = quote.PublicId;
        return Ok(quote);
    }

    /// <summary>The buyer asks the supplier to revise (price, lead time, quantity, …) (<c>quote.requestRevision</c>).</summary>
    [HttpPost("quotes/{quoteId:guid}/revision-requests")]
    [Idempotent]
    [Authorize(Policy = Permissions.QuoteRequestRevision)]
    [EnableRateLimiting(RateLimitPolicies.QuoteRespond)]
    public async Task<ActionResult<QuoteSummaryResponse>> RequestRevision(
        Guid quoteId, [FromBody] RequestQuoteRevisionRequest request, CancellationToken ct)
    {
        QuoteSummaryResponse quote = await quotes.RequestRevisionAsync(SubjectId(), quoteId, request, ct);
        HttpContext.Items[IdempotencyFilter.ReferenceItemsKey] = quote.PublicId;
        return Ok(quote);
    }

    /// <summary>
    /// The buyer accepts a quote and the order is created in the same transaction
    /// (api.md §2.7/§2.8, business-flows §10). A retry of the same key replays the
    /// original order id; a second accept — concurrent or later — is
    /// <c>QUOTE_ALREADY_ACCEPTED</c>, and an elapsed validity is <c>QUOTE_EXPIRED</c>.
    /// </summary>
    [HttpPost("quotes/{quoteId:guid}/accept")]
    [Idempotent]
    [Authorize(Policy = Permissions.QuoteAccept)]
    [EnableRateLimiting(RateLimitPolicies.QuoteAccept)]
    public async Task<ActionResult<AcceptQuoteResponse>> Accept(Guid quoteId, CancellationToken ct)
    {
        AcceptQuoteResponse order = await orders.AcceptQuoteAsync(SubjectId(), quoteId, ct);
        HttpContext.Items[IdempotencyFilter.ReferenceItemsKey] = order.OrderId;
        return Created($"/api/v1/orders/{order.OrderId}", order);
    }

    /// <summary>The authenticated subject; [Authorize] guarantees it (1.41).</summary>
    private Guid SubjectId()
        => Guid.Parse(User.FindFirstValue(AuthConstants.SubjectClaim)
            ?? throw new InvalidOperationException(
                "Authorized action without a subject claim; the permission pipeline must have rejected the ticket."));
}
