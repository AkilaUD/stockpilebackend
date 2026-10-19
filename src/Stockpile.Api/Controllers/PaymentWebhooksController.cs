using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Stockpile.Api.Filters;
using Stockpile.Api.Idempotency;
using Stockpile.Api.RateLimiting;
using Stockpile.Application.Subscriptions;
using Stockpile.Application.Subscriptions.Payments;
using Stockpile.Contracts.Subscriptions;

namespace Stockpile.Api.Controllers;

/// <summary>
/// Payment-provider callbacks (business-flows §12, api.md §2.10). The caller is a
/// payment provider, not a user, so the endpoint is anonymous and exempt from the
/// browser CSRF check — and is instead authenticated by an HMAC signature over the
/// exact raw body, verified by <see cref="PaymentWebhookService"/> before anything
/// is parsed or read. It is <c>[Idempotent]</c> (an <c>Idempotency-Key</c> is
/// required) AND idempotent on the payment itself, so however a provider retries,
/// one delivery produces one state change.
/// </summary>
[ApiController]
[ApiVersion(1.0)]
[Route("api/v{version:apiVersion}/webhooks/payments")]
public sealed class PaymentWebhooksController(PaymentWebhookService webhooks) : ControllerBase
{
    /// <summary>A real provider event is a few hundred bytes; anything near this is not one.</summary>
    private const int MaxBodyBytes = 64 * 1024;

    /// <summary>
    /// Receives one signed delivery. <c>401 WEBHOOK_SIGNATURE_INVALID</c> for a bad
    /// or stale signature (nothing changes); <c>200</c> with
    /// <c>replayed: true</c> when the payment was already settled.
    /// </summary>
    [HttpPost("{provider}")]
    [Idempotent]
    [AllowAnonymous] // the provider has no session; the signature is the credential (T6.1)
    [WebhookCsrfExempt]
    [EnableRateLimiting(RateLimitPolicies.PaymentWebhook)]
    [RequestSizeLimit(MaxBodyBytes, Order = -1000)] // set BEFORE the idempotency filter buffers the body
    public async Task<ActionResult<WebhookAckResponse>> Receive(string provider, CancellationToken cancellationToken)
    {
        // The signature covers the raw bytes, so they are read here untouched — never
        // re-serialised from a bound model.
        using MemoryStream buffer = new();
        if (Request.Body.CanSeek)
        {
            Request.Body.Position = 0;
        }

        await Request.Body.CopyToAsync(buffer, cancellationToken);

        string idempotencyKey = Request.Headers["Idempotency-Key"].ToString().Trim();
        WebhookAckResponse ack = await webhooks.ProcessAsync(
            provider,
            buffer.ToArray(),
            Request.Headers[WebhookSignature.HeaderName].ToString(),
            idempotencyKey,
            cancellationToken);

        HttpContext.Items[IdempotencyFilter.ReferenceItemsKey] = idempotencyKey;
        return Ok(ack);
    }
}
