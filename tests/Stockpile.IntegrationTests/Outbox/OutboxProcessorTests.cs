using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Stockpile.Application.Outbox;
using Stockpile.Domain.Entities.Platform;
using Stockpile.Domain.Enums;
using Stockpile.Infrastructure.Persistence;
using Stockpile.IntegrationTests.Testing;

namespace Stockpile.IntegrationTests.Outbox;

/// <summary>
/// Task 1.28 — outbox claim loop (architecture §10.1) against real
/// Stockpile_Test via the shared host's <see cref="IOutboxProcessor"/>: a row
/// with no consumer is delivered and marked Processed; a recording consumer
/// receives its payload; a failing consumer schedules exponential backoff and
/// dead-letters at the attempt ceiling. Each test inserts its own row and
/// asserts on the latest of its type so re-runs are independent.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class OutboxProcessorTests
{
    private readonly StockpileApiFactory _factory;

    /// <summary>Shares the one API host (one migrate + seed of Stockpile_Test) across tests.</summary>
    public OutboxProcessorTests(StockpileApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Event_without_a_registered_consumer_is_delivered_and_processed()
    {
        await InsertRowAsync("test.event.unhandled", "{\"v\":1}");

        using (var scope = _factory.Services.CreateScope())
        {
            var processor = scope.ServiceProvider.GetRequiredService<IOutboxProcessor>();
            int processed = await processor.ProcessBatchAsync(10);
            Assert.True(processed >= 1);
        }

        Assert.Equal(OutboxMessageStatus.Processed, LatestRowOf("test.event.unhandled").Status);
    }

    [Fact]
    public async Task Registered_consumer_receives_the_delivery()
    {
        RecordingTestEventHandler.Delivered.Clear();
        await InsertRowAsync("test.record.ok", "{\"a\":2}");

        using (var scope = _factory.Services.CreateScope())
        {
            var processor = scope.ServiceProvider.GetRequiredService<IOutboxProcessor>();
            await processor.ProcessBatchAsync(10);
        }

        OutboxDelivery delivery = Assert.Single(RecordingTestEventHandler.Delivered);
        Assert.Equal("test.record.ok", delivery.EventType);
        Assert.Equal("{\"a\":2}", delivery.PayloadJson);
        Assert.Equal(OutboxMessageStatus.Processed, LatestRowOf("test.record.ok").Status);
    }

    [Fact]
    public async Task Consumer_failure_schedules_exponential_backoff()
    {
        await InsertRowAsync("test.fail.boom", "{}");

        using (var scope = _factory.Services.CreateScope())
        {
            var processor = scope.ServiceProvider.GetRequiredService<IOutboxProcessor>();
            await processor.ProcessBatchAsync(10);
        }

        OutboxMessage row = LatestRowOf("test.fail.boom");
        Assert.Equal(OutboxMessageStatus.Pending, row.Status);
        Assert.Equal(1, row.Attempts);
        Assert.True(row.NextAttemptAt > DateTime.UtcNow, "backoff should push the next attempt into the future");
    }

    [Fact]
    public async Task Failure_at_the_attempt_ceiling_dead_letters_the_row()
    {
        await InsertRowAsync("test.fail.boom2", "{}", attempts: 7);

        using (var scope = _factory.Services.CreateScope())
        {
            var processor = scope.ServiceProvider.GetRequiredService<IOutboxProcessor>();
            await processor.ProcessBatchAsync(10);
        }

        OutboxMessage row = LatestRowOf("test.fail.boom2");
        Assert.Equal(OutboxMessageStatus.DeadLettered, row.Status);
        Assert.Equal(8, row.Attempts);
        Assert.Contains("consumer exploded", row.DeadLetterReason);
    }

    private async Task InsertRowAsync(string eventType, string payload, int attempts = 0)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StockpileDbContext>();
        // The shared Stockpile_Test database accumulates outbox rows across
        // test runs and phases (aggregates append domain-event rows no test
        // dispatcher consumes). The claim batch is TOP(10) ORDER BY Id, so
        // stale rows can starve a test's own row out of the batch. Only these
        // tests write/read the real OutboxMessages table, so wipe it here to
        // keep every test hermetic and deterministic.
        await db.Database.ExecuteSqlRawAsync("DELETE FROM [OutboxMessages]");
        db.OutboxMessages.Add(new OutboxMessage
        {
            EventType = eventType,
            Payload = payload,
            Attempts = attempts,
            NextAttemptAt = DateTime.UtcNow.AddSeconds(-1), // due immediately
        });
        await db.SaveChangesAsync();
    }

    private OutboxMessage LatestRowOf(string eventType)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StockpileDbContext>();
        return db.OutboxMessages
            .AsNoTracking()
            .Where(message => message.EventType == eventType)
            .OrderByDescending(message => message.Id)
            .First();
    }
}

/// <summary>Test-only outbox consumer that records everything it receives.</summary>
public sealed class RecordingTestEventHandler : IOutboxConsumer
{
    public static readonly ConcurrentBag<OutboxDelivery> Delivered = [];

    /// <inheritdoc />
    public bool Supports(string eventType) => eventType == "test.record.ok";

    /// <inheritdoc />
    public Task HandleAsync(OutboxDelivery delivery, CancellationToken cancellationToken = default)
    {
        Delivered.Add(delivery);
        return Task.CompletedTask;
    }
}

/// <summary>Test-only outbox consumer that throws on anything it handles.</summary>
public sealed class FailingTestEventHandler : IOutboxConsumer
{
    /// <inheritdoc />
    public bool Supports(string eventType) =>
        eventType.StartsWith("test.fail.", StringComparison.Ordinal);

    /// <inheritdoc />
    public Task HandleAsync(OutboxDelivery delivery, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("consumer exploded");
}
