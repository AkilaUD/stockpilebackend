using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Stockpile.Application.Authentication;

/// <summary>
/// RFC 6238 TOTP — the second factor behind the MFA-ready endpoints
/// (threat-model §13.4 line 174, task 1.44): 6 digits, 30 s step, HMAC-SHA1
/// (the interoperability default every authenticator app speaks — the
/// otpauth URI's declared algorithm), a ±1 step window for clock skew, and a
/// constant-time code comparison (a guess is never timed against the computed
/// value). The secret is a 160-bit CSPRNG value rendered as base32 without
/// padding — exactly what an authenticator app scans from the otpauth URI.
/// All members are pure: time arrives as a parameter, so the RFC 6238 test
/// vectors and the window boundaries are testable without a clock.
/// </summary>
public static class Totp
{
    /// <summary>Seconds per step (the otpauth URI's `period`).</summary>
    public const int StepSeconds = 30;

    /// <summary>Code length (the otpauth URI's `digits`).</summary>
    public const int Digits = 6;

    /// <summary>Entropy per secret (RFC 4226 §4 recommends ≥ 128 bits; 160 matches the classic seed).</summary>
    private const int SecretBytes = 20;

    /// <summary>10^Digits — with <see cref="Digits"/> = 6 the modulus and zero-padded width go together.</summary>
    private const int Modulus = 1_000_000;

    /// <summary>RFC 4648 base32 — uppercase, unpadded (what otpauth URIs carry).</summary>
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>
    /// A fresh enrolment secret: <see cref="SecretBytes"/> bytes from the CSPRNG
    /// rendered as a 32-character base32 string. Generated server-side only —
    /// the client never contributes entropy to its own second factor.
    /// </summary>
    public static string GenerateSecret() => Base32Encode(RandomNumberGenerator.GetBytes(SecretBytes));

    /// <summary>
    /// The code for the step containing <paramref name="timestamp"/> — the
    /// server's expectation, and the value integration tests compute from the
    /// enrol response to drive the real verify endpoint.
    /// </summary>
    public static string ComputeCode(string base32Secret, DateTimeOffset timestamp)
    {
        byte[] key = DecodeSecret(base32Secret)
            ?? throw new InvalidOperationException("The TOTP secret is not valid base32.");
        return ComputeCode(key, StepAt(timestamp));
    }

    /// <summary>
    /// Validates a submitted code against the stored secret for the step of
    /// <paramref name="timestamp"/> and its immediate neighbours (±1 step —
    /// authenticator clocks drift; a wider window would multiply the guess
    /// surface). A malformed code or secret simply fails — never throws, so a
    /// corrupted row is a denial, not a 500.
    /// </summary>
    public static bool Verify(string? base32Secret, string? code, DateTimeOffset timestamp)
    {
        if (string.IsNullOrEmpty(base32Secret) || code is not { Length: Digits } || !IsDigits(code))
        {
            return false;
        }

        byte[]? key = DecodeSecret(base32Secret);
        if (key is null)
        {
            return false; // corrupted stored value — deny, never crash
        }

        byte[] submitted = Encoding.ASCII.GetBytes(code);
        long step = StepAt(timestamp);
        bool matched = false;

        // Fixed three candidates, each compared in constant time: the number of
        // comparisons never depends on where (or whether) a match was found.
        for (long candidate = step - 1; candidate <= step + 1; candidate++)
        {
            if (candidate < 0)
            {
                continue;
            }

            byte[] expected = Encoding.ASCII.GetBytes(ComputeCode(key, candidate));
            matched |= CryptographicOperations.FixedTimeEquals(expected, submitted);
        }

        return matched;
    }

    /// <summary>
    /// The scan target for the authenticator app:
    /// <c>otpauth://totp/{issuer}:{account}?secret=…&amp;issuer=…</c> with the
    /// standards-declared parameters, so any RFC 6238 app imports it directly.
    /// </summary>
    public static string BuildOtpauthUri(string issuer, string account, string base32Secret)
    {
        string escapedIssuer = Uri.EscapeDataString(issuer);
        string escapedAccount = Uri.EscapeDataString(account);

        // The declared parameters carry ints, so formatting runs invariant;
        // the label/secret part is pure concatenation (culture-free by nature).
        string parameters = FormattableString.Invariant(
            $"&algorithm=SHA1&digits={Digits}&period={StepSeconds}");
        return $"otpauth://totp/{escapedIssuer}:{escapedAccount}?secret={base32Secret}&issuer={escapedIssuer}{parameters}";
    }

    private static long StepAt(DateTimeOffset timestamp)
        => timestamp.ToUnixTimeSeconds() / StepSeconds;

    /// <summary>RFC 4226 §5.3 dynamic truncation over HMAC-SHA1(key, counter).</summary>
    // CA5350 — deliberately HMAC-SHA1: RFC 6238 fixes it as THE interoperable
    // TOTP algorithm (the otpauth URI declares `algorithm=SHA1`, and every
    // authenticator app speaks exactly that — a "stronger" algorithm would fail
    // to import and the §13.4 MFA-ready architecture would be uninteroperable).
    // The SHA-1 weakness is collision finding, which does not apply to HMAC
    // verification of a truncated time-code.
#pragma warning disable CA5350 // Do not use weak cryptographic algorithms
    private static string ComputeCode(byte[] key, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(counter, (ulong)step);

        byte[] hash = HMACSHA1.HashData(key, counter);
        int offset = hash[^1] & 0x0F;
        int binary = ((hash[offset] & 0x7F) << 24)
            | (hash[offset + 1] << 16)
            | (hash[offset + 2] << 8)
            | hash[offset + 3];

        return (binary % Modulus).ToString("D6", CultureInfo.InvariantCulture);
    }
#pragma warning restore CA5350

    private static string Base32Encode(ReadOnlySpan<byte> bytes)
    {
        var builder = new StringBuilder((bytes.Length * 8 + 4) / 5);
        int buffer = 0;
        int bitsLeft = 0;

        foreach (byte value in bytes)
        {
            buffer = (buffer << 8) | value;
            bitsLeft += 8;
            while (bitsLeft >= 5)
            {
                bitsLeft -= 5;
                builder.Append(Base32Alphabet[(buffer >> bitsLeft) & 0x1F]);
            }

            buffer &= (1 << bitsLeft) - 1; // keep only the pending bits — nothing above can ever be read again
        }

        if (bitsLeft > 0)
        {
            builder.Append(Base32Alphabet[(buffer << (5 - bitsLeft)) & 0x1F]);
        }

        return builder.ToString();
    }

    /// <summary>Decodes a stored/generated secret; null on anything malformed (deny, never throw).</summary>
    private static byte[]? DecodeSecret(string base32Secret)
    {
        var output = new List<byte>(base32Secret.Length * 5 / 8);
        int buffer = 0;
        int bitsLeft = 0;

        foreach (char character in base32Secret)
        {
            int index = Base32Alphabet.IndexOf(character);
            if (index < 0)
            {
                // Standard padding ends a well-formed body; anything else is corruption.
                return character is '=' ? output.ToArray() : null;
            }

            buffer = (buffer << 5) | index;
            bitsLeft += 5;
            if (bitsLeft >= 8)
            {
                bitsLeft -= 8;
                output.Add((byte)(buffer >> bitsLeft));
                buffer &= (1 << bitsLeft) - 1;
            }
        }

        // Leftover bits must be zero-padding (< 5 bits) — a longer residue means
        // a corrupted value, not a secret.
        return bitsLeft < 5 ? output.ToArray() : null;
    }

    private static bool IsDigits(string code)
    {
        foreach (char character in code)
        {
            if (character is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }
}
