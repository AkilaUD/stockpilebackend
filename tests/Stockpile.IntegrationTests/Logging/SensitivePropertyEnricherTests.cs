using Serilog;
using Serilog.Core;
using Serilog.Events;
using Stockpile.Api.Logging;

namespace Stockpile.IntegrationTests.Logging;

/// <summary>
/// Task 1.23 — security matrix Z4: a password, token or reset secret must
/// never reach a sink, even when a future log statement templates one by
/// accident. The enricher scrubs by property name at every nesting level.
/// </summary>
public sealed class SensitivePropertyEnricherTests
{
    [Fact]
    public void Redacts_top_level_sensitive_properties_and_keeps_the_rest()
    {
        var logEvent = Write(logger => logger.Information(
            "Login attempt for {User} with {Password} ({Token})",
            "kasun", "hunter2", "abc123"));

        Assert.Equal("[REDACTED]", ScalarOf(logEvent, "Password"));
        Assert.Equal("[REDACTED]", ScalarOf(logEvent, "Token"));
        Assert.Equal("kasun", ScalarOf(logEvent, "User"));

        // Rendered messages (console template) read the same redacted values,
        // so the marker reaches text sinks too — not just structured ones.
        Assert.DoesNotContain("hunter2", logEvent.RenderMessage());
    }

    [Fact]
    public void Redacts_sensitive_properties_nested_in_a_destructured_object()
    {
        var logEvent = Write(logger => logger.Information(
            "Attempt {@Attempt}",
            new { UserName = "kasun", ResetSecret = "s3cr3t", Attempts = 1 }));

        var structure = Assert.IsType<StructureValue>(logEvent.Properties["Attempt"]);
        var nested = structure.Properties.ToDictionary(p => p.Name, p => p.Value);

        Assert.Equal("[REDACTED]", Assert.IsType<ScalarValue>(nested["ResetSecret"]).Value);
        Assert.Equal("kasun", Assert.IsType<ScalarValue>(nested["UserName"]).Value);
        Assert.Equal(1, Assert.IsType<ScalarValue>(nested["Attempts"]).Value);
    }

    [Fact]
    public void Redacts_sensitive_properties_inside_sequence_elements()
    {
        var logEvent = Write(logger => logger.Information(
            "Entries {@Entries}",
            new[] { new { AccessToken = "t-1" }, new { AccessToken = "t-2" } }));

        var sequence = Assert.IsType<SequenceValue>(logEvent.Properties["Entries"]);
        Assert.Equal(2, sequence.Elements.Count);

        foreach (var element in sequence.Elements)
        {
            var accessToken = Assert.IsType<StructureValue>(element)
                .Properties.Single(p => p.Name == "AccessToken").Value;
            Assert.Equal("[REDACTED]", Assert.IsType<ScalarValue>(accessToken).Value);
        }
    }

    [Fact]
    public void Matches_property_names_case_insensitively()
    {
        var logEvent = Write(logger => logger.Information("pw {password}", "hunter2"));

        Assert.Equal("[REDACTED]", ScalarOf(logEvent, "password"));
    }

    private static LogEvent Write(Action<ILogger> statement)
    {
        var sink = new CollectingSink();
        using var logger = new LoggerConfiguration()
            .Enrich.With(new SensitivePropertyEnricher())
            .WriteTo.Sink(sink)
            .CreateLogger();

        statement(logger);

        var logEvent = Assert.Single(sink.Events);
        return logEvent;
    }

    private static object? ScalarOf(LogEvent logEvent, string name) =>
        Assert.IsType<ScalarValue>(logEvent.Properties[name]).Value;

    private sealed class CollectingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}
