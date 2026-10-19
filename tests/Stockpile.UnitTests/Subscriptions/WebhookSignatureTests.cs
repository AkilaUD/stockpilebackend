using System.Text;
using Stockpile.Application.Subscriptions.Payments;

namespace Stockpile.UnitTests.Subscriptions;

/// <summary>
/// The webhook signature scheme (business-flows §12): <c>t=&lt;unix&gt;,v1=&lt;hex HMAC-SHA-256 of "{t}.{body}"&gt;</c>.
/// Verification must accept exactly the genuine, fresh, untampered delivery and
/// fail closed on everything else.
/// </summary>
public sealed class WebhookSignatureTests
{
    private const string Secret = "unit-test-secret";
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("""{"providerRef":"p-1","amount":100}""");
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(300);

    [Fact]
    public void A_signature_verifies_for_the_same_secret_and_body()
    {
        string header = WebhookSignature.Sign(Secret, Now, Body);

        Assert.True(WebhookSignature.Verify(Secret, header, Body, Now, Tolerance));
    }

    [Fact]
    public void The_header_has_the_documented_shape()
    {
        string header = WebhookSignature.Sign(Secret, Now, Body);

        Assert.StartsWith($"t={Now.ToUnixTimeSeconds()},v1=", header, StringComparison.Ordinal);
        string hex = header[(header.IndexOf("v1=", StringComparison.Ordinal) + 3)..];
        Assert.Equal(64, hex.Length);
        Assert.Equal(hex.ToLowerInvariant(), hex);
    }

    [Fact]
    public void A_tampered_body_does_not_verify()
    {
        string header = WebhookSignature.Sign(Secret, Now, Body);
        byte[] tampered = Encoding.UTF8.GetBytes("""{"providerRef":"p-1","amount":999}""");

        Assert.False(WebhookSignature.Verify(Secret, header, tampered, Now, Tolerance));
    }

    [Fact]
    public void A_different_secret_does_not_verify()
    {
        string header = WebhookSignature.Sign("another-secret", Now, Body);

        Assert.False(WebhookSignature.Verify(Secret, header, Body, Now, Tolerance));
    }

    [Theory]
    [InlineData(-299, true)]
    [InlineData(299, true)]
    [InlineData(300, true)]
    [InlineData(-301, false)]
    [InlineData(301, false)]
    public void The_timestamp_must_be_within_tolerance_in_either_direction(int offsetSeconds, bool accepted)
    {
        string header = WebhookSignature.Sign(Secret, Now.AddSeconds(offsetSeconds), Body);

        Assert.Equal(accepted, WebhookSignature.Verify(Secret, header, Body, Now, Tolerance));
    }

    [Fact]
    public void A_timestamp_edited_into_the_header_breaks_the_signature()
    {
        string header = WebhookSignature.Sign(Secret, Now.AddSeconds(-200), Body);
        string forged = header.Replace(
            $"t={Now.AddSeconds(-200).ToUnixTimeSeconds()}",
            $"t={Now.ToUnixTimeSeconds()}",
            StringComparison.Ordinal);

        Assert.False(WebhookSignature.Verify(Secret, forged, Body, Now, Tolerance));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("garbage")]
    [InlineData("t=abc,v1=00")]
    [InlineData("t=1792411200")]
    [InlineData("v1=abcdef")]
    [InlineData("t=1792411200,v1=not-hex")]
    [InlineData("t=-5,v1=00")]
    [InlineData("=,=")]
    public void A_missing_or_malformed_header_fails_closed(string? header)
    {
        Assert.False(WebhookSignature.Verify(Secret, header, Body, Now, Tolerance));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void A_missing_secret_never_verifies(string? secret)
    {
        string header = WebhookSignature.Sign(Secret, Now, Body);

        Assert.False(WebhookSignature.Verify(secret, header, Body, Now, Tolerance));
    }

    [Fact]
    public void Signing_with_an_empty_secret_is_refused()
    {
        Assert.Throws<ArgumentException>(() => WebhookSignature.Sign(string.Empty, Now, Body));
    }

    [Fact]
    public void A_truncated_signature_does_not_verify()
    {
        string header = WebhookSignature.Sign(Secret, Now, Body);
        string truncated = header[..^2];

        Assert.False(WebhookSignature.Verify(Secret, truncated, Body, Now, Tolerance));
    }
}
