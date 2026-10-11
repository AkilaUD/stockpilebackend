using System.Net;
using System.Text.Json;
using Stockpile.IntegrationTests.Testing;

namespace Stockpile.IntegrationTests.Versioning;

/// <summary>
/// Task 1.31 — versioning + OpenAPI over real HTTP: the versioned
/// /api/v1 route resolves, responses report api-supported-versions,
/// an undeclared version is rejected, and the OpenAPI JSON document is
/// generated FROM CODE (api.md §4) on the Development-shaped test host.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class VersioningApiTests
{
    private readonly StockpileApiFactory _factory;

    /// <summary>Shares the one API host (one migrate + seed of Stockpile_Test) across tests.</summary>
    public VersioningApiTests(StockpileApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Versioned_route_resolves_v1_and_reports_supported_versions()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/api/v1/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("api-supported-versions", out var supported));
        Assert.Equal("1.0", Assert.Single(supported!));
    }

    [Fact]
    public async Task Undeclared_version_is_rejected()
    {
        using var client = _factory.CreateClient();

        // Only version 1.0 is declared; v2 must not silently serve v1 endpoints.
        using var response = await client.GetAsync("/api/v2/health/live");

        Assert.InRange((int)response.StatusCode, 400, 499);
    }

    [Fact]
    public async Task OpenApi_document_is_generated_from_code()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement root = document.RootElement;

        string? openApi = root.GetProperty("openapi").GetString();
        Assert.NotNull(openApi);
        Assert.StartsWith("3.", openApi);

        Assert.Equal("Stockpile API", root.GetProperty("info").GetProperty("title").GetString());
        Assert.Equal("1.0", root.GetProperty("info").GetProperty("version").GetString());

        // The document comes from the code annotations: the versioned health
        // endpoints exist as paths, not a hand-maintained spec.
        Assert.Contains(
            root.GetProperty("paths").EnumerateObject(),
            path => path.Name.StartsWith("/api/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Scalar_ui_is_served_for_the_versioned_openapi_document()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/scalar/v1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
    }
}
