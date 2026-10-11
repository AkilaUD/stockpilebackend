using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Stockpile.Domain.Common;
using Stockpile.Domain.Entities.Platform;
using Stockpile.Domain.Enums;
using Stockpile.Infrastructure.Outbox;

namespace Stockpile.IntegrationTests.Outbox;

/// <summary>
/// Task 1.28 — outbox write interceptor (architecture §10.1) against an
/// ad-hoc InMemory context: raised domain events become OutboxMessage rows in
/// the SAME SaveChanges as the aggregate change, are never flushed for
/// untouched aggregates, and a malformed event fails the save without being
/// consumed.
/// </summary>
public sealed class OutboxInterceptorTests
{
    [Fact]
    public async Task Raised_events_become_outbox_rows_in_the_same_save()
    {
        using var context = CreateContext();
        var aggregate = new TestAggregate();
        var first = new DomainEvent(DefaultTime, null, "corr-1", "test.event.raised", "{\"x\":1}");
        var second = new DomainEvent(DefaultTime, null, "corr-1", "test.event.raised", "{\"x\":2}");
        aggregate.Raise(first);
        aggregate.Raise(second);
        context.Aggregates.Add(aggregate);

        await context.SaveChangesAsync();

        List<OutboxMessage> rows = context.OutboxMessages.ToList();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row =>
        {
            Assert.Equal("test.event.raised", row.EventType);
            Assert.Equal("corr-1", row.CorrelationId);
            Assert.Equal(DefaultTime, row.OccurredAt);
            Assert.Equal(OutboxMessageStatus.Pending, row.Status);
            Assert.Equal(DefaultTime, row.NextAttemptAt);
        });
        Assert.Contains(rows, row => row.Payload == "{\"x\":1}");
        Assert.Contains(rows, row => row.Payload == "{\"x\":2}");
        Assert.Empty(aggregate.Events); // consumed at capture — no double flush
    }

    [Fact]
    public async Task Events_on_untouched_aggregates_are_not_flushed_by_other_saves()
    {
        using var context = CreateContext();
        var aggregate = new TestAggregate();
        aggregate.Raise(new DomainEvent(DefaultTime, null, "corr-1", "test.event.first", "{}"));
        context.Aggregates.Add(aggregate);
        await context.SaveChangesAsync(); // first flush persisted

        aggregate.Raise(new DomainEvent(DefaultTime, null, "corr-1", "test.event.second", "{}"));
        await context.Noise.AddAsync(new NoiseRow());
        await context.SaveChangesAsync(); // unrelated save — aggregate unchanged

        OutboxMessage only = Assert.Single(context.OutboxMessages.ToList());
        Assert.Equal("test.event.first", only.EventType);
        Assert.Single(aggregate.Events); // second event still pending, not lost
    }

    [Fact]
    public async Task A_malformed_event_fails_the_save_without_consuming_it()
    {
        using var context = CreateContext();
        var aggregate = new TestAggregate();
        aggregate.Raise(new DomainEvent(DefaultTime, null, "corr-1", string.Empty, "{}"));
        context.Aggregates.Add(aggregate);

        await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());

        Assert.Empty(context.OutboxMessages.ToList());
        Assert.Single(aggregate.Events); // nothing cleared — the aggregate keeps its event
    }

    private static readonly DateTime DefaultTime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static TestDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .AddInterceptors(new OutboxSaveChangesInterceptor())
            .Options;
        return new TestDbContext(options);
    }

    /// <summary>Stand-in context: same interceptor, minimal tables.</summary>
    private sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
    {
        public DbSet<TestAggregate> Aggregates => Set<TestAggregate>();
        public DbSet<NoiseRow> Noise => Set<NoiseRow>();
        public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            // In-memory has no table for the event queue — it lives only on the
            // aggregate until the interceptor materialises it.
            modelBuilder.Entity<TestAggregate>().Ignore(aggregate => aggregate.Events);
    }

    private sealed class TestAggregate : IHasDomainEvents
    {
        public int Id { get; set; }

        private readonly List<DomainEvent> _events = [];

        public IReadOnlyCollection<DomainEvent> Events => _events;

        public void Raise(DomainEvent domainEvent) => _events.Add(domainEvent);

        public void ClearEvents() => _events.Clear();
    }

    private sealed class NoiseRow
    {
        public int Id { get; set; }
    }
}
