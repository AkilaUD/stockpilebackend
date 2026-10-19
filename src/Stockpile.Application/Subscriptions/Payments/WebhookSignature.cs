using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Stockpile.Application.Subscriptions.Payments;

/// <summary>
/// The platform's webhook signature scheme (business-flows §12): the header
/// <c>X-Stockpile-Signature: t=&lt;unix seconds&gt;,v1=&lt;hex&gt;</c> carries a timestamp
/// and an HMAC-SHA-256 over <c>"{t}.{raw body}"</c> keyed by the shared secret.
/// Verification is constant-time, fails closed on any malformed input, and rejects
/// a timestamp outside the tolerance window in EITHER direction, so a captured
/// delivery cannot be replayed later.
/// </summary>
public static class WebhookSignature
{
    public const string HeaderName = "X-Stockpile-Signature";

    /// <summary>Builds a header value for <paramref name="body"/> at <paramref name="timestamp"/> (used by the dev simulator and tests).</summary>
    public static string Sign(string secret, DateTimeOffset timestamp, ReadOnlySpan<byte> body)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);

        long seconds = timestamp.ToUnixTimeSeconds();
        string signature = Convert.ToHexString(Compute(secret, seconds, body)).ToLowerInvariant();
        return $"t={seconds.ToString(CultureInfo.InvariantCulture)},v1={signature}";
    }

    /// <summary>True only when the header parses, the timestamp is within tolerance and the HMAC matches.</summary>
    public static bool Verify(
        string? secret, string? header, ReadOnlySpan<byte> body, DateTimeOffset now, TimeSpan tolerance)
    {
        if (string.IsNullOrEmpty(secret) || string.IsNullOrWhiteSpace(header))
        {
            return false;
        }

        long? timestamp = null;
        byte[]? provided = null;
        foreach (string part in header.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = part.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                return false;
            }

            string name = part[..separator];
            string value = part[(separator + 1)..];
            if (name == "t" && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed))
            {
                timestamp = parsed;
            }
            else if (name == "v1")
            {
                try
                {
                    provided = Convert.FromHexString(value);
                }
                catch (FormatException)
                {
                    return false;
                }
            }
        }

        if (timestamp is null || provided is null)
        {
            return false;
        }

        long skew = Math.Abs(now.ToUnixTimeSeconds() - timestamp.Value);
        if (skew > (long)tolerance.TotalSeconds)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(Compute(secret, timestamp.Value, body), provided);
    }

    private static byte[] Compute(string secret, long timestamp, ReadOnlySpan<byte> body)
    {
        byte[] prefix = Encoding.UTF8.GetBytes(timestamp.ToString(CultureInfo.InvariantCulture) + ".");
        byte[] message = new byte[prefix.Length + body.Length];
        prefix.CopyTo(message, 0);
        body.CopyTo(message.AsSpan(prefix.Length));
        return HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), message);
    }
}
