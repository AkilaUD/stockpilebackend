using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Stockpile.Application.Audit;
using Stockpile.Application.Authentication;
using Stockpile.Application.Common.Errors;
using Stockpile.Application.Subscriptions.Payments;
using Stockpile.Contracts.Errors;
using Stockpile.Contracts.Subscriptions;
using Stockpile.Domain.Entities.Subscriptions;
using Stockpile.Domain.Enums;

namespace Stockpile.Application.Subscriptions;

/// <summary>
/// Receives payment-provider webhooks (business-flows §12). Order matters:
/// <list type="number">
///   <item>The signature is verified over the exact raw bytes BEFORE the body is
///   parsed or any row is read, and a failure is a bare 401 — nothing in the
///   response says why, and no state changes.</item>
///   <item>The payload names a payment by (provider, providerRef); an unknown pair is
///   404, never created on a webhook's say-so.</item>
///   <item>A payment that is no longer <c>Pending</c> has already been settled: the
///   delivery is acknowledged as a replay and changes nothing (one state change per
///   payment, whatever the number of deliveries or their <c>Idempotency-Key</c>s).</item>
///   <item>The signed amount and currency must equal what the platform asked for.</item>
///   <item>Settlement is the same code path an operator's manual mark-paid uses.</item>
/// </list>
/// A signature failure is logged, not written to the database: the endpoint runs
/// inside the idempotency transaction, which rolls back on the 401 — so an
/// unauthenticated caller can neither fill a table nor reserve an idempotency key.
/// </summary>
public sealed partial class PaymentWebhookService(
    ISubscriptionsStore store,
    IEnumerable<IPaymentProvider> providers,
    IAuditService audit,
    ICurrentRequestContext requestContext,
    ILogger<PaymentWebhookService> logger)
{
    /// <summary><c>SubscriptionPayment.RawPayload</c> column width; a larger body is rejected rather than truncated.</summary>
    internal const int MaxStoredPayloadLength = 4000;

    private const string SucceededEvent = "payment.succeeded";
    private const string FailedEvent = "payment.failed";

    public async Task<WebhookAckResponse> ProcessAsync(
        string providerName,
        ReadOnlyMemory<byte> rawBody,
        string? signatureHeader,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        IPaymentProvider provider = providers.FirstOrDefault(
                candidate => candidate.IsEnabled
                    && string.Equals(candidate.Name, providerName, StringComparison.OrdinalIgnoreCase))
            ?? throw new AppException(ErrorCodes.ResourceNotFound);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (!provider.VerifyWebhook(signatureHeader, rawBody.Span, now))
        {
            LogSignatureRejected(logger, provider.Name);
            throw new AppException(ErrorCodes.WebhookSignatureInvalid);
        }

        string body = Encoding.UTF8.GetString(rawBody.Span);
        WebhookPayload payload = Parse(body);

        PaymentRecord record = await store.FindPaymentForUpdateAsync(provider.Name, payload.ProviderRef, cancellationToken)
            ?? throw new AppException(ErrorCodes.ResourceNotFound);
        SubscriptionPayment payment = record.Payment;

        if (payment.Status != PaymentStatus.Pending)
        {
            return new WebhookAckResponse(Replayed: true);
        }

        if (payload.Amount != payment.Amount
            || !string.Equals(payload.Currency, payment.CurrencyCode, StringComparison.OrdinalIgnoreCase))
        {
            throw new AppException(
                ErrorCodes.ValidationFailed,
                [new FieldError("amount", "amount_mismatch", "The signed amount does not match the invoice.")]);
        }

        payment.SignatureValid = true;
        payment.IdempotencyKey = idempotencyKey;
        payment.RawPayload = body;
        payment.OccurredAt = now.UtcDateTime;

        string auditAction;
        if (payload.Succeeded)
        {
            payment.Status = PaymentStatus.Succeeded;
            bool settled = SubscriptionBilling.Settle(
                record.Invoice.Invoice,
                record.Invoice.Subscription,
                now.UtcDateTime,
                payment.ProviderRef,
                actorId: null,
                requestContext.CorrelationId);

            // A payment that lands on an invoice already closed (paid by hand meanwhile, or voided
            // by a cancellation) is recorded as received money but moves no subscription state.
            auditAction = settled ? "subscription.payment_succeeded" : "subscription.payment_on_closed_invoice";
        }
        else
        {
            payment.Status = PaymentStatus.Failed;
            auditAction = "subscription.payment_failed";
        }

        await store.SaveAsync(cancellationToken);
        await provider.RecordWebhookAsync(
            new WebhookRecord(payment.ProviderRef, idempotencyKey, body, now),
            cancellationToken);
        await audit.RecordAsync(
            new AuditEntry(auditAction, "SubscriptionPayment", payment.PublicId.ToString(), AuditResult.Success),
            cancellationToken);

        return new WebhookAckResponse(Replayed: false);
    }

    /// <summary>Provider-agnostic reading of the verified body; anything else is a malformed request.</summary>
    private static WebhookPayload Parse(string body)
    {
        if (body.Length is 0 or > MaxStoredPayloadLength)
        {
            throw new AppException(ErrorCodes.MalformedRequest);
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new AppException(ErrorCodes.MalformedRequest);
            }

            string? providerRef = root.TryGetProperty("providerRef", out JsonElement refElement)
                && refElement.ValueKind == JsonValueKind.String
                    ? refElement.GetString()
                    : null;
            string? eventName = root.TryGetProperty("event", out JsonElement eventElement)
                && eventElement.ValueKind == JsonValueKind.String
                    ? eventElement.GetString()
                    : null;
            string? currency = root.TryGetProperty("currency", out JsonElement currencyElement)
                && currencyElement.ValueKind == JsonValueKind.String
                    ? currencyElement.GetString()
                    : null;

            if (string.IsNullOrWhiteSpace(providerRef)
                || string.IsNullOrWhiteSpace(currency)
                || !root.TryGetProperty("amount", out JsonElement amountElement)
                || amountElement.ValueKind != JsonValueKind.Number
                || !amountElement.TryGetDecimal(out decimal amount)
                || eventName is not (SucceededEvent or FailedEvent))
            {
                throw new AppException(ErrorCodes.MalformedRequest);
            }

            return new WebhookPayload(providerRef, eventName == SucceededEvent, amount, currency);
        }
        catch (JsonException)
        {
            throw new AppException(ErrorCodes.MalformedRequest);
        }
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Warning,
        Message = "Payment webhook for provider {Provider} rejected: signature did not verify.")]
    private static partial void LogSignatureRejected(ILogger logger, string provider);

    private sealed record WebhookPayload(string ProviderRef, bool Succeeded, decimal Amount, string Currency);
}
