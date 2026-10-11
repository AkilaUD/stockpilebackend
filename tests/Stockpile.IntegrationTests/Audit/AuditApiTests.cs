using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Stockpile.Application.Audit;
using Stockpile.Domain.Entities.Platform;
using Stockpile.Domain.Enums;
using Stockpile.Infrastructure.Persistence;
using Stockpile.IntegrationTests.Testing;

namespace Stockpile.IntegrationTests.Audit;

/// <summary>
/// Task 1.29 — audit + security-event persistence over the shared host
/// (security-test-matrix X7/X9/Z4): the scoped <see cref="IAuditService"/>
/// records rows with ambient enrichment over real HTTP; the scoped
/// <see cref="ISecurityEventService"/> persists standalone; and the append-only
/// guarantee (X9) is enforced by the source-scan architecture test.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class AuditApiTests
{
    private readonly StockpileApiFactory _factory;

    /// <summary>Shares the one API host (one migrate + seed of Stockpile_Test) across tests.</summary>
    public AuditApiTests(StockpileApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Recording_audit_over_http_persists_with_ambient_enrichment()
    {
        using var client = AntiforgeryTestSupport.HttpsClient(_factory);
        string token = await AntiforgeryTestSupport.AcquireTokenAsync(client);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/_test/audit")
        {
            Content = JsonContent.Create(new
            {
                action = "user.register",
                targetType = "User",
                targetId = (string?)null,
                result = "Success",
            }),
        };
        request.Headers.TryAddWithoutValidation("X-Correlation-Id", "corr-1234567890a");
        request.Headers.UserAgent.TryParseAdd("stockpile-test-agent");

        using var response = await AntiforgeryTestSupport.SendUnsafeAsync(client, request, token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("corr-1234567890a", response.Headers.GetValues("X-Correlation-Id").Single());

        AuditLog row = LatestAuditRow();
        Assert.Equal("user.register", row.Action);
        Assert.Equal("User", row.TargetType);
        Assert.Null(row.TargetId);
        Assert.Equal(AuditResult.Success, row.Result);
        Assert.Equal("corr-1234567890a", row.CorrelationId);
        Assert.Equal("stockpile-test-agent", row.UserAgent);
        Assert.NotEqual(default, row.OccurredAt);
    }

    [Fact]
    public async Task Audit_outside_a_request_uses_system_defaults()
    {
        // No HTTP context: the scoped ICurrentRequestContext (ApiRequestContext)
        // sees no request and yields the system actor + nulls, exactly what the
        // Worker faces when an outbox consumer audits.
        using var scope = _factory.Services.CreateScope();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditService>();

        await audit.RecordAsync(new AuditEntry("rfq.submit", "RFQ", null, AuditResult.Denied, "{\"base\":true}"));

        AuditLog row = LatestAuditRow();
        Assert.Equal("rfq.submit", row.Action);
        Assert.Equal(AuditResult.Denied, row.Result);
        Assert.Null(row.ActorId);
        Assert.Null(row.IpAddress);
        Assert.Null(row.CorrelationId);
        Assert.Equal("{\"base\":true}", row.Metadata);
    }

    [Fact]
    public async Task Security_event_service_persists_a_row()
    {
        Guid userId = Guid.NewGuid();
        using var scope = _factory.Services.CreateScope();
        var securityEvents = scope.ServiceProvider.GetRequiredService<ISecurityEventService>();

        await securityEvents.RecordAsync(
            SecurityEventType.LockoutTriggered,
            userId: userId,
            detail: "5 failed attempts",
            metadata: "{\"source\":\"login\",\"attempts\":5}");

        SecurityEvent? row = LatestSecurityEvent();
        Assert.NotNull(row);
        Assert.Equal(SecurityEventType.LockoutTriggered, row.Type);
        Assert.Equal(userId, row.UserId);
        Assert.Equal("5 failed attempts", row.Detail);
        Assert.Equal("{\"source\":\"login\",\"attempts\":5}", row.Metadata);
    }

    private AuditLog LatestAuditRow()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StockpileDbContext>();
        return db.AuditLogs.AsNoTracking().OrderByDescending(row => row.Id).First();
    }

    private SecurityEvent? LatestSecurityEvent()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StockpileDbContext>();
        return db.SecurityEvents.AsNoTracking().OrderByDescending(row => row.Id).FirstOrDefault();
    }
}

/// <summary>Test-only <see cref="ISecurityEventService"/> that records what it is asked to write.</summary>
public sealed class RecordingSecurityEventService : ISecurityEventService
{
    public sealed record Recorded(SecurityEventType Type, Guid? UserId, string? Detail, string? Metadata);

    public static readonly ConcurrentQueue<Recorded> Written = new();

    /// <inheritdoc />
    public Task RecordAsync(
        SecurityEventType type,
        Guid? userId = null,
        string? detail = null,
        string? metadata = null,
        CancellationToken cancellationToken = default)
    {
        Written.Enqueue(new Recorded(type, userId, detail, metadata));
        return Task.CompletedTask;
    }
}
