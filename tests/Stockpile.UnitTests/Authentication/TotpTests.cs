using Stockpile.Application.Authentication;

namespace Stockpile.UnitTests.Authentication;

/// <summary>
/// Task 1.44 — RFC 6238 TOTP, the second factor behind the MFA-ready
/// endpoints (threat-model §13.4 line 174). The implementation is pure, so the
/// RFC's own Appendix B vectors pin the cryptography (HMAC-SHA1, dynamic
/// truncation, 6 digits), the ±1 step window pins the clock-skew allowance,
/// and the malformed-input cases pin fail-closed behaviour — a corrupted
/// secret or a garbage code is a denial, never an exception.
/// </summary>
public sealed class TotpTests
{
    /// <summary>The RFC 4648 base32 of the ASCII seed "12345678901234567890" — the canonical TOTP test key.</summary>
    private const string RfcSeed = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";

    [Theory]
    [InlineData(59, "287082")] // RFC 6238 B.1 — 8-digit 94287082, first 6 of the last 6 = 287082
    [InlineData(1111111109, "081804")] // B.2 — 07081804
    [InlineData(1111111111, "050471")] // B.3 — 14050471
    [InlineData(1234567890, "005924")] // B.4 — 89005924
    [InlineData(2000000000, "279037")] // B.5 — 69279037
    [InlineData(20000000000, "353130")] // B.6 — 65353130
    public void The_rfc_6238_vectors_match(long unixSeconds, string expectedSixDigits)
    {
        string code = Totp.ComputeCode(RfcSeed, DateTimeOffset.FromUnixTimeSeconds(unixSeconds));
        Assert.Equal(expectedSixDigits, code);
    }

    [Fact]
    public void The_window_is_one_step_each_side_and_no_wider()
    {
        // A fixed instant 10 s into its step (1 000 000 000 = 33 333 333 × 30
        // + 10), so the ±1 boundaries are exercised away from step edges.
        DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1_000_000_000);
        string current = Totp.ComputeCode(RfcSeed, now);
        string previousStep = Totp.ComputeCode(RfcSeed, now.AddSeconds(-Totp.StepSeconds));
        string nextStep = Totp.ComputeCode(RfcSeed, now.AddSeconds(Totp.StepSeconds));

        Assert.True(Totp.Verify(RfcSeed, current, now), "the current step's code must verify");
        Assert.True(Totp.Verify(RfcSeed, previousStep, now), "clock skew: the previous step's code must verify");
        Assert.True(Totp.Verify(RfcSeed, nextStep, now), "clock skew: the next step's code must verify");

        string tooOld = Totp.ComputeCode(RfcSeed, now.AddSeconds(-2 * Totp.StepSeconds));
        string tooNew = Totp.ComputeCode(RfcSeed, now.AddSeconds(2 * Totp.StepSeconds));
        Assert.False(Totp.Verify(RfcSeed, tooOld, now), "two steps back is outside the guessing window");
        Assert.False(Totp.Verify(RfcSeed, tooNew, now), "two steps ahead is outside the guessing window");
    }

    [Fact]
    public void A_wrong_code_never_verifies()
    {
        DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1_000_000_000);
        string real = Totp.ComputeCode(RfcSeed, now);

        // Every digit perturbed — a wrong guess fails regardless of position.
        for (int position = 0; position < 6; position++)
        {
            char flipped = real[position] == '0' ? '1' : '0';
            string wrong = string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{real[..position]}{flipped}{real[(position + 1)..]}");
            Assert.False(Totp.Verify(RfcSeed, wrong, now), $"position {position}'s perturbed code must fail");
        }
    }

    [Fact]
    public void Malformed_input_fails_closed_without_throwing()
    {
        DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1_000_000_000);

        Assert.False(Totp.Verify(RfcSeed, null, now));
        Assert.False(Totp.Verify(RfcSeed, string.Empty, now));
        Assert.False(Totp.Verify(RfcSeed, "12345", now)); // 5 digits
        Assert.False(Totp.Verify(RfcSeed, "1234567", now)); // 7 digits
        Assert.False(Totp.Verify(RfcSeed, "ABCDEF", now)); // letters
        Assert.False(Totp.Verify(RfcSeed, "123 45", now)); // not the filter's shape
        Assert.False(Totp.Verify(null, "123456", now)); // no secret — pending account
        Assert.False(Totp.Verify(string.Empty, "123456", now));
        Assert.False(Totp.Verify("not-base32-!!", "123456", now)); // corrupted row
        Assert.False(Totp.Verify("MZXW6YTBOI=======", "123456", now)); // valid base32, wrong key
    }

    [Fact]
    public void Generated_secrets_are_32_base32_characters_and_verify_round_trip()
    {
        string first = Totp.GenerateSecret();
        string second = Totp.GenerateSecret();

        Assert.Equal(32, first.Length);
        Assert.All(first, character => Assert.Contains(character, "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567"));
        Assert.NotEqual(first, second); // CSPRNG: enrolment collisions are not a thing

        DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1_000_000_000);
        string code = Totp.ComputeCode(first, now);
        Assert.True(Totp.Verify(first, code, now), "a code from a generated secret must round-trip");
        Assert.False(Totp.Verify(second, code, now), "a foreign secret must reject the same code");
    }

    [Fact]
    public void The_otpauth_uri_declares_the_standard_parameters()
    {
        string uri = Totp.BuildOtpauthUri("Stockpile", "nuwan@example.test", RfcSeed);

        Assert.StartsWith("otpauth://totp/Stockpile:nuwan%40example.test?secret=", uri);
        Assert.Contains($"secret={RfcSeed}", uri);
        Assert.Contains("issuer=Stockpile", uri);
        Assert.Contains("algorithm=SHA1", uri);
        Assert.Contains($"digits={Totp.Digits}", uri);
        Assert.Contains($"period={Totp.StepSeconds}", uri);
    }
}
