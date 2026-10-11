using System.Net;
using System.Text.Json;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Stockpile.IntegrationTests.Testing;

namespace Stockpile.IntegrationTests.Health;

/// <summary>
/// Task 1.24 — liveness answers without probing dependencies; readiness probes
/// SQL Server; both payloads stay minimal (no exception or dependency detail —
/// the endpoints are unauthenticated, security-threat-model T4.5). Also pins
/// that the OpenTelemetry providers are actually registered.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class ObservabilityApiTests
{
    private readonly StockpileApiFactory _factory;

    /// <summary>Shares the one API host (one migrate + seed of Stockpile_Test) across tests.</summary>
    public ObservabilityApiTests(StockpileApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Liveness_reports_healthy_without_running_dependency_checks()
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/api/v1/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await AssertHealthyMinimalPayloadAsync(response);
    }

    [Fact]
    public async Task Readiness_probes_sql_server_and_reports_healthy_when_reachable()
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/api/v1/health/ready");

        // Stockpile_Test is reachable in the test environment → healthy.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await AssertHealthyMinimalPayloadAsync(response);
    }

    [Fact]
    public void OpenTelemetry_tracer_and_meter_providers_are_registered()
    {
        Assert.NotNull(_factory.Services.GetService(typeof(TracerProvider)));
        Assert.NotNull(_factory.Services.GetService(typeof(MeterProvider)));
    }

    private static async Task AssertHealthyMinimalPayloadAsync(HttpResponseMessage response)
    {
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = payload.RootElement;
        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        Assert.Equal("healthy", root.GetProperty("status").GetString());
        Assert.True(root.GetProperty("uptimeSeconds").GetInt64() >= 0);

        // T4.5: exactly { status, uptimeSeconds } — no check names, no
        // exception text, no infrastructure fingerprints.
        var names = root.EnumerateObject()
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal);
        Assert.Equal(["status", "uptimeSeconds"], names);
    }
}
