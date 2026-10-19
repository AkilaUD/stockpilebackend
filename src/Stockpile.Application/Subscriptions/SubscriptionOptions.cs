namespace Stockpile.Application.Subscriptions;

/// <summary>
/// Billing knobs (<c>Subscriptions</c> section). Defaults state only what the
/// reference model implies: no free trial is invented, so a new subscription is
/// <c>Trial</c> with a zero-day window — i.e. no entitlements — until its first
/// invoice is paid.
/// </summary>
public sealed class SubscriptionOptions
{
    public const string SectionName = "Subscriptions";

    /// <summary>Free-trial days granted at subscribe; 0 = none (entitlements start at the first payment).</summary>
    public int TrialDays { get; set; }

    /// <summary>Days from issue until an invoice falls due.</summary>
    public int InvoiceDueDays { get; set; } = 7;
}

/// <summary>Payment-provider configuration (<c>Payments</c> section). No live card gateway exists; the dev file-sink is the only provider.</summary>
public sealed class PaymentOptions
{
    public const string SectionName = "Payments";

    /// <summary>Provider used when initiating a payment.</summary>
    public string DefaultProvider { get; set; } = FileSinkProviderName;

    /// <summary>Webhook timestamp tolerance in seconds (±5 minutes, business-flows §12).</summary>
    public int WebhookToleranceSeconds { get; set; } = 300;

    public FileSinkPaymentOptions FileSink { get; set; } = new();

    /// <summary>Provider code of the dev file-sink.</summary>
    public const string FileSinkProviderName = "filesink";
}

/// <summary>The dev <c>FileSinkPaymentProvider</c> (business-flows §12): writes payment records to disk, never moves money.</summary>
public sealed class FileSinkPaymentOptions
{
    /// <summary>Off unless explicitly enabled — the sink is a development tool and the filesystem is ephemeral in hosted environments.</summary>
    public bool Enabled { get; set; }

    /// <summary>Shared secret that signs the simulated provider's webhooks. Empty = every webhook is rejected.</summary>
    public string? WebhookSecret { get; set; }

    /// <summary>Directory for the records, relative to the content root; defaults to <c>{Storage:Root}/payments</c>.</summary>
    public string? Directory { get; set; }
}
