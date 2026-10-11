using Microsoft.Extensions.Logging;
using Stockpile.Api.RateLimiting;

namespace Stockpile.IntegrationTests.RateLimiting;

/// <summary>
/// Task 1.26 — threat-model §7 abuse detection: a burst is
/// <see cref="RateLimitBurstMonitor"/>'s configured number of rejected
/// requests from one partition inside the burst window. The signal fires once
/// per window fill (not per rejection) and re-arms after the window drains, so
/// operators see patterns rather than 429 noise. Task 1.29 attaches
/// SecurityEvent persistence; here the log sink is observed.
/// </summary>
public sealed class RateLimitBurstMonitorTests
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        public List<string> Messages { get; } = [];

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Messages);

        public void Dispose()
        {
        }
    }

    private sealed class CapturingLogger(List<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            messages.Add($"{logLevel}:{formatter(state, exception)}");
    }

    /// <summary>Wall clock the monitor reads; advanced manually between records.</summary>
    private sealed class StubTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;

        public StubTimeProvider(DateTimeOffset start) => _now = start;

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }

    [Fact]
    public void Burst_fires_once_when_the_window_fills()
    {
        var (monitor, logger, clock) = CreateMonitor();

        RecordAt(monitor, clock, 0, 5); // two rejections: not yet a burst
        Assert.Empty(logger.Messages);

        RecordAt(monitor, clock, 5);
        var burst = Assert.Single(logger.Messages);
        Assert.Contains("Rate-limit burst", burst, StringComparison.Ordinal);
        Assert.Contains("auth.login", burst, StringComparison.Ordinal);
        Assert.Contains("ip:127.0.0.1", burst, StringComparison.Ordinal);
    }

    [Fact]
    public void Burst_does_not_refire_while_the_partition_stays_over_the_threshold()
    {
        var (monitor, logger, clock) = CreateMonitor();

        RecordAt(monitor, clock, 0, 5, 10); // fills the window
        Assert.Single(logger.Messages);

        RecordAt(monitor, clock, 15, 20, 25); // still over — no new signal
        Assert.Single(logger.Messages);
    }

    [Fact]
    public void Burst_refires_after_the_window_drains()
    {
        var (monitor, logger, clock) = CreateMonitor();

        RecordAt(monitor, clock, 0, 5, 10); // burst 1
        clock.Advance(TimeSpan.FromSeconds(61)); // whole window passes
        RecordAt(monitor, clock, 0, 5, 10); // burst 2

        Assert.Equal(2, logger.Messages.Count);
    }

    [Fact]
    public void Rejections_more_than_a_window_apart_never_form_a_burst()
    {
        var (monitor, logger, clock) = CreateMonitor();

        RecordAt(monitor, clock, 0, 61);

        Assert.Empty(logger.Messages);
    }

    private static (RateLimitBurstMonitor Monitor, CapturingLoggerFactory Logger, StubTimeProvider Clock)
        CreateMonitor()
    {
        var logger = new CapturingLoggerFactory();
        var clock = new StubTimeProvider(new DateTimeOffset(2026, 10, 7, 0, 0, 0, TimeSpan.Zero));
        var monitor = new RateLimitBurstMonitor(burstThreshold: 3, burstWindow: Window, timeProvider: clock);
        monitor.AttachLogger(logger);
        return (monitor, logger, clock);
    }

    private static void RecordAt(RateLimitBurstMonitor monitor, StubTimeProvider clock, params int[] offsetsInSeconds)
    {
        foreach (var offset in offsetsInSeconds)
        {
            if (offset > 0)
            {
                clock.Advance(TimeSpan.FromSeconds(offset));
            }

            monitor.Record("auth.login", "ip:127.0.0.1");
        }
    }
}
