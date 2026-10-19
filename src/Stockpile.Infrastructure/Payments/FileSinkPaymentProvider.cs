using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Stockpile.Application.Subscriptions;
using Stockpile.Application.Subscriptions.Payments;

namespace Stockpile.Infrastructure.Payments;

/// <summary>
/// The development payment provider (business-flows §12): it moves no money. Each
/// payment intent is written as a JSON file, and each VERIFIED webhook is written
/// next to it, so a developer can see exactly what a real gateway would have
/// received and sent. A payment is "made" by posting a correctly signed
/// <c>payment.succeeded</c> body to the webhook endpoint — the same door a real
/// gateway would use, so the signature, replay and amount checks are exercised
/// for real.
/// <para>
/// Disabled unless <c>Payments:FileSink:Enabled</c> is true. The target directory is
/// resolved by the composition root; files are named from provider references we
/// generated (never from request input) and written under that directory only.
/// </para>
/// </summary>
public sealed class FileSinkPaymentProvider(IOptions<PaymentOptions> options, FileSinkLocation location) : IPaymentProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <inheritdoc />
    public string Name => PaymentOptions.FileSinkProviderName;

    /// <inheritdoc />
    public bool IsEnabled => options.Value.FileSink.Enabled;

    /// <inheritdoc />
    public async Task<PaymentIntent> CreateIntentAsync(PaymentIntentRequest request, CancellationToken cancellationToken)
    {
        if (!IsEnabled)
        {
            throw new InvalidOperationException("The file-sink payment provider is disabled.");
        }

        string providerRef = $"fs_{Guid.NewGuid():N}";
        string fileName = $"{providerRef}.intent.json";

        Directory.CreateDirectory(location.Directory);
        string json = JsonSerializer.Serialize(
            new
            {
                providerRef,
                paymentId = request.PaymentId,
                invoiceNumber = request.InvoiceNumber,
                amount = request.Amount,
                currency = request.Currency,
                createdAt = DateTimeOffset.UtcNow,
            },
            JsonOptions);
        await File.WriteAllTextAsync(Path.Combine(location.Directory, fileName), json, cancellationToken);

        return new PaymentIntent(
            providerRef,
            $"Development file-sink: intent recorded as payments/{fileName}. "
            + "Settle it by POSTing a signed payment.succeeded webhook to /api/v1/webhooks/payments/filesink.");
    }

    /// <inheritdoc />
    public bool VerifyWebhook(string? signatureHeader, ReadOnlySpan<byte> rawBody, DateTimeOffset now)
    {
        // Disabled or secretless = nothing verifies (fail closed).
        return IsEnabled
            && WebhookSignature.Verify(
                options.Value.FileSink.WebhookSecret,
                signatureHeader,
                rawBody,
                now,
                TimeSpan.FromSeconds(options.Value.WebhookToleranceSeconds));
    }

    /// <inheritdoc />
    public async Task RecordWebhookAsync(WebhookRecord record, CancellationToken cancellationToken)
    {
        // The reference was matched to a stored payment we created, but name the file from
        // its file-safe part only so a record can never escape the sink directory.
        string safeRef = Path.GetFileName(record.ProviderRef);
        string fileName = string.Create(
            CultureInfo.InvariantCulture,
            $"{safeRef}.webhook-{record.ReceivedAt:yyyyMMddHHmmssfff}-{record.IdempotencyKey}.json");

        Directory.CreateDirectory(location.Directory);
        string json = JsonSerializer.Serialize(
            new
            {
                providerRef = record.ProviderRef,
                idempotencyKey = record.IdempotencyKey,
                receivedAt = record.ReceivedAt,
                body = record.RawBody,
            },
            JsonOptions);
        await File.WriteAllTextAsync(
            Path.Combine(location.Directory, Path.GetFileName(fileName)), json, cancellationToken);
    }
}

/// <summary>Where the file-sink writes, resolved once by the composition root.</summary>
public sealed record FileSinkLocation(string Directory);
