namespace Stockpile.Application.Subscriptions.Payments;

/// <summary>
/// Payment-provider port (business-flows §12). A provider turns "this invoice should
/// be paid" into a provider reference, and tells us whether a webhook really came
/// from it. The platform never claims to process every transaction: a real gateway
/// is a later integration, and the dev <c>FileSinkPaymentProvider</c> only records
/// what a gateway would have done.
/// </summary>
public interface IPaymentProvider
{
    /// <summary>Stable lower-case provider code, stored on every payment row.</summary>
    string Name { get; }

    /// <summary>False when the provider is configured off — it then neither initiates payments nor accepts webhooks.</summary>
    bool IsEnabled { get; }

    /// <summary>Registers an intent to pay with the provider and returns its reference.</summary>
    Task<PaymentIntent> CreateIntentAsync(PaymentIntentRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Verifies a webhook's <c>X-Stockpile-Signature</c> over the exact raw bytes
    /// received. Must be constant-time and must fail closed (no secret configured
    /// means no webhook verifies).
    /// </summary>
    bool VerifyWebhook(string? signatureHeader, ReadOnlySpan<byte> rawBody, DateTimeOffset now);

    /// <summary>
    /// Records a VERIFIED webhook delivery (the file-sink writes it to disk for
    /// inspection). Never called for an unverified body.
    /// </summary>
    Task RecordWebhookAsync(WebhookRecord record, CancellationToken cancellationToken);
}

/// <summary>What the platform asks a provider to collect. Carries no payer identity — only the invoice's own figures.</summary>
public sealed record PaymentIntentRequest(Guid PaymentId, string InvoiceNumber, decimal Amount, string Currency);

/// <summary>The provider's answer: its reference for the attempt and the payer's next step.</summary>
public sealed record PaymentIntent(string ProviderRef, string? Instructions);

/// <summary>A verified webhook delivery, as received.</summary>
public sealed record WebhookRecord(string ProviderRef, string IdempotencyKey, string RawBody, DateTimeOffset ReceivedAt);
