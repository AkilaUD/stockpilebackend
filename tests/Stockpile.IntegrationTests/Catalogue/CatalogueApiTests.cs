using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Stockpile.IntegrationTests.Testing;

namespace Stockpile.IntegrationTests.Catalogue;

/// <summary>
/// Anonymous public catalogue HTTP surface (api.md §2.3) — summary, category
/// tree, and search must answer 200 with seeded data after DbSeeder runs.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class CatalogueApiTests
{
    private const string ApiBase = "/api/v1";

    private readonly StockpileApiFactory _factory;

    public CatalogueApiTests(StockpileApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Summary_and_categories_are_anonymous_and_non_empty()
    {
        using HttpClient client = AntiforgeryTestSupport.HttpsClient(_factory);

        using HttpResponseMessage summary = await client.GetAsync($"{ApiBase}/catalog/summary");
        Assert.Equal(HttpStatusCode.OK, summary.StatusCode);
        using JsonDocument summaryBody = JsonDocument.Parse(await summary.Content.ReadAsStringAsync());
        Assert.True(summaryBody.RootElement.GetProperty("products").GetInt32() > 0);

        using HttpResponseMessage categories = await client.GetAsync($"{ApiBase}/catalog/categories");
        Assert.Equal(HttpStatusCode.OK, categories.StatusCode);
        var tree = await categories.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, tree.ValueKind);
        Assert.True(tree.GetArrayLength() > 0);
    }

    [Fact]
    public async Task Search_with_a_query_returns_a_paged_envelope()
    {
        using HttpClient client = AntiforgeryTestSupport.HttpsClient(_factory);

        using HttpResponseMessage response = await client.GetAsync($"{ApiBase}/catalog/search?q=cement&page=1&pageSize=12");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Array, body.RootElement.GetProperty("items").ValueKind);
    }

    [Fact]
    public async Task Category_listing_and_filtered_search_stay_composable()
    {
        // Regression for FilteredProducts: category subtree + verified/in-stock
        // filters must remain SQL subqueries (no Guid ToList materialisation).
        using HttpClient client = AntiforgeryTestSupport.HttpsClient(_factory);

        using HttpResponseMessage tree = await client.GetAsync($"{ApiBase}/catalog/categories");
        Assert.Equal(HttpStatusCode.OK, tree.StatusCode);
        var roots = await tree.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(roots.GetArrayLength() > 0);
        string slug = roots[0].GetProperty("slug").GetString()
            ?? throw new InvalidOperationException("Seeded category tree has no root slug.");

        using HttpResponseMessage category = await client.GetAsync(
            $"{ApiBase}/catalog/categories/{Uri.EscapeDataString(slug)}?page=1&pageSize=12");
        Assert.Equal(HttpStatusCode.OK, category.StatusCode);

        using HttpResponseMessage search = await client.GetAsync(
            $"{ApiBase}/catalog/search?q=a&verifiedOnly=true&page=1&pageSize=12");
        Assert.Equal(HttpStatusCode.OK, search.StatusCode);
        using JsonDocument body = JsonDocument.Parse(await search.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Array, body.RootElement.GetProperty("items").ValueKind);
    }

    [Fact]
    public async Task Unknown_product_slug_is_404()
    {
        using HttpClient client = AntiforgeryTestSupport.HttpsClient(_factory);

        using HttpResponseMessage response = await client.GetAsync(
            $"{ApiBase}/catalog/products/this-slug-does-not-exist-{Guid.NewGuid():N}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
