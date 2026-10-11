using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Stockpile.Api.RateLimiting;
using Stockpile.Application.Audit;
using Stockpile.Domain.Enums;
using Stockpile.IntegrationTests.Audit;

namespace Stockpile.IntegrationTests.Audit;

/// <summary>
/// Task 1.29 — burst → security-event pipeline (Z4): a burst monitor crossing
/// drains exactly one persistent signal per window, and the
/// <see cref="BurstSecurityEventWriter"/> shapes it as a <c>RateLimitHit</c>
/// with a human detail and metadata that must never carry a secret key.
/// </summary>
public sealed class BurstSecurityEventTests
{
    [Fact]
    public async Task DrainAsync_persists_each_crossed_burst_exactly_once()
    {
        var monitor = new RateLimitBurstMonitor(burstThreshold: 3, burstWindow: TimeSpan.FromSeconds(60));
        var persisted = new List<RateLimitBurstMonitor.BurstSignal>();

        monitor.Record("login", "partition-a");
        monitor.Record("login", "partition-a"); // below threshold — no signal yet
        await monitor.DrainAsync((signal, _) =>
        {
            persisted.Add(signal);
            return Task.CompletedTask;
        }, CancellationToken.None);
        Assert.Empty(persisted);

        monitor.Record("login", "partition-a"); // window fills → crosses
        await monitor.DrainAsync((signal, _) =>
        {
            persisted.Add(signal);
            return Task.CompletedTask;
        }, CancellationToken.None);

        var burst = Assert.Single(persisted);
        Assert.Equal("login", burst.Policy);
        Assert.Equal("partition-a", burst.Partition);
        Assert.Equal(3, burst.BurstThreshold);
        Assert.Equal(60, burst.BurstWindowSeconds);

        await monitor.DrainAsync((signal, _) =>
        {
            persisted.Add(signal);
            return Task.CompletedTask;
        }, CancellationToken.None);
        Assert.Single(persisted); // the drain cleared the queue — nothing re-persisted
    }

    [Fact]
    public async Task Burst_writer_creates_RateLimitHit_without_sensitive_keys()
    {
        RecordingSecurityEventService.Written.Clear();
        var provider = new ServiceCollection()
            .AddSingleton<ISecurityEventService, RecordingSecurityEventService>()
            .BuildServiceProvider();

        var writer = BurstSecurityEventWriter.For(provider);
        await writer(new RateLimitBurstMonitor.BurstSignal("login", "sub:00000000-0000-0000-0000-000000000001", 3, 60), CancellationToken.None);

        RecordingSecurityEventService.Recorded recorded =
            Assert.Single(RecordingSecurityEventService.Written);
        Assert.Equal(SecurityEventType.RateLimitHit, recorded.Type);
        Assert.Contains("login", recorded.Detail);

        using JsonDocument metadata = JsonDocument.Parse(recorded.Metadata!);
        Assert.Equal("login", metadata.RootElement.GetProperty("policy").GetString());
        Assert.Equal(3, metadata.RootElement.GetProperty("burstThreshold").GetInt32());

        // Z4: metadata carries policy/partition/threshold only — never a
        // sensitive property name (scrubbing list mirrors the log enricher).
        string[] sensitiveKeys = ["password", "passphrase", "pwd", "token", "secret", "authorization", "cookie", "credential", "apikey"];
        string lower = recorded.Metadata!.ToLowerInvariant();
        Assert.DoesNotContain(sensitiveKeys, key => lower.Contains(key, StringComparison.Ordinal));
    }
}
