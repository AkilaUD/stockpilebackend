using System.Security.Cryptography;

namespace Stockpile.Application.Authentication;

/// <summary>
/// Task 1.39 — the exponential lockout backoff with jitter (threat-model T1.1,
/// §13.4 line 168: "threshold + window + exponential delay + admin unlock;
/// repeated lockouts raise an alert rather than permanent lock"). Each lockout
/// episode lasts <c>base × 2^(episode−1)</c> minutes, multiplied by a ±20 %
/// jitter draw so a scripted attacker can never dial in the exact window, and
/// capped at <c>Auth:LockoutMaxMinutes</c> — the ladder grows, but a locked
/// account is always eventually usable by its owner (jitter keeps the release
/// moment unpredictable, which is the point).
/// <para>
/// Pure and stateless so the unit suite can assert the exact bounds
/// (LockoutBackoffTests); the ±20 % band is chosen wide enough to be useful
/// but narrow enough that the ACCOUNT_LOCKED "about N minutes" message stays
/// honest.
/// </para>
/// </summary>
public static class LockoutBackoff
{
    /// <summary>Jitter half-width: the final window is ±20 % of the exponential base window.</summary>
    public const double JitterHalfWidth = 0.20;

    /// <summary>
    /// The backoff window for lockout <paramref name="episode"/> (1-based):
    /// base × 2^(episode−1) minutes scaled by <paramref name="jitterMultiplier"/>
    /// (the 0.8–1.2 multiplier from <see cref="RandomJitterMultiplier"/>), and the
    /// result never exceeds <paramref name="maxMinutes"/> — the cap is applied
    /// AFTER jitter so the ladder's ceiling is absolute.
    /// </summary>
    public static TimeSpan Window(int episode, double baseMinutes, double maxMinutes, double jitterMultiplier)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(episode);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(baseMinutes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxMinutes);

        double exponential = baseMinutes * Math.Pow(2, Math.Max(0, episode - 1));
        double jittered = exponential * jitterMultiplier;
        return TimeSpan.FromMinutes(Math.Min(jittered, maxMinutes));
    }

    /// <summary>
    /// A uniform jitter multiplier on [0.80, 1.20) drawn from the CSPRNG — the
    /// per-lockout uncertainty that defeats scripted retry timing.
    /// </summary>
    public static double RandomJitterMultiplier()
        => 0.8 + 0.4 * (RandomNumberGenerator.GetInt32(0, 1000) / 1000.0);
}