using Stockpile.Application.Authentication;

namespace Stockpile.UnitTests.Authentication;

/// <summary>
/// Task 1.39 — the exponential lockout backoff with jitter (threat-model T1.1,
/// §13.4 line 168). The math is pure so the exact bounds are asserted here:
/// base × 2^(episode−1) minutes, scaled by a ±20 % jitter draw, with an
/// ABSOLUTE cap at Auth:LockoutMaxMinutes — the ladder grows between episodes
/// (jitter can never invert the growth) but the ceiling holds even at the top
/// jitter draw, so repeated lockouts raise an alert rather than become a
/// permanent denial.
/// </summary>
public sealed class LockoutBackoffTests
{
    private const double Base = 15;
    private const double Max = 1440;

    [Fact]
    public void Episode_one_is_the_base_window_inside_the_jitter_band()
    {
        TimeSpan low = LockoutBackoff.Window(1, Base, Max, 0.8);
        TimeSpan high = LockoutBackoff.Window(1, Base, Max, 1.2);

        Assert.Equal(TimeSpan.FromMinutes(12), low);  // 15 × 0.8
        Assert.Equal(TimeSpan.FromMinutes(18), high); // 15 × 1.2
    }

    [Fact]
    public void Each_episode_doubles_its_predecessors_window()
    {
        // The WORST jitter of episode N+1 must still beat the BEST jitter of
        // episode N — 15×2×0.8 = 24 > 15×1.2 = 18 — so the ladder is monotonic
        // across the jitter band and a re-offending attacker can never buy a
        // shorter window by losing the coin flip.
        Assert.Equal(TimeSpan.FromMinutes(24), LockoutBackoff.Window(2, Base, Max, 0.8));
        Assert.Equal(TimeSpan.FromMinutes(36), LockoutBackoff.Window(2, Base, Max, 1.2));
        Assert.Equal(TimeSpan.FromMinutes(48), LockoutBackoff.Window(3, Base, Max, 0.8));
        Assert.Equal(TimeSpan.FromMinutes(72), LockoutBackoff.Window(3, Base, Max, 1.2));

        for (int episode = 1; episode < 10; episode++)
        {
            double worstNext = Math.Min(Base * Math.Pow(2, episode) * 0.8, Max);
            double bestCurrent = Math.Min(Base * Math.Pow(2, episode - 1) * 1.2, Max);
            Assert.True(worstNext >= bestCurrent,
                $"episode {episode + 1}'s worst window ({worstNext}) fell behind episode {episode}'s best ({bestCurrent})");
        }
    }

    [Fact]
    public void The_cap_is_absolute_even_at_the_top_jitter()
    {
        // A 10th episode would be 15×2^9 = 7680 min raw; the cap applies AFTER
        // jitter, so no draw can push the window past maxMinutes.
        Assert.Equal(TimeSpan.FromMinutes(Max), LockoutBackoff.Window(10, Base, Max, 1.2));
        Assert.Equal(TimeSpan.FromMinutes(Max), LockoutBackoff.Window(10, Base, Max, 0.8));

        double capped = LockoutBackoff.Window(4, Base, 100, 1.2).TotalMinutes;
        Assert.Equal(100, capped); // 15×8×1.2 = 144 raw → clamped to the cap
    }

    [Fact]
    public void A_capped_episode_never_regresses_below_the_previous_one()
    {
        // Repeated lockouts top out at the cap — permanently at the ceiling,
        // never bouncing back down to a shorter window.
        double previous = LockoutBackoff.Window(8, Base, Max, 0.8).TotalMinutes;
        for (int episode = 9; episode <= 20; episode++)
        {
            double window = LockoutBackoff.Window(episode, Base, Max, 0.8).TotalMinutes;
            Assert.True(window >= previous, $"episode {episode} regressed below episode {episode - 1}");
            previous = window;
        }

        Assert.Equal(Max, previous);
    }

    [Fact]
    public void Random_jitter_stays_inside_the_documented_band()
    {
        // The ±20 % band is what keeps the "about N minutes" lockout message
        // honest while still defeating scripted retry timing.
        for (int draw = 0; draw < 500; draw++)
        {
            double jitter = LockoutBackoff.RandomJitterMultiplier();
            Assert.InRange(jitter, 0.8, 1.1999);
        }
    }

    [Theory]
    [InlineData(0)] // episode is 1-based
    [InlineData(-3)]
    public void A_non_positive_episode_is_a_programming_error(int episode)
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => LockoutBackoff.Window(episode, Base, Max, 1.0));

    [Fact]
    public void Non_positive_base_or_cap_is_a_programming_error()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LockoutBackoff.Window(1, 0, Max, 1.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => LockoutBackoff.Window(1, -1, Max, 1.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => LockoutBackoff.Window(1, Base, 0, 1.0));
    }
}
