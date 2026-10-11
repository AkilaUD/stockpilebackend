using System.Net;
using System.Net.Http.Json;
using System.Text;
using Stockpile.Contracts.Errors;
using Stockpile.IntegrationTests.Testing;

namespace Stockpile.IntegrationTests.Errors;

/// <summary>
/// Tasks 1.18–1.21 over real HTTP: every failure path answers the api.md §1.4
/// envelope with the status registered for its code — and never a stack trace
/// (§1.5). Also pins correlation-id propagation through the whole pipeline.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class ErrorEnvelopeApiTests
{
    private const string JsonBody = "application/json";

    private readonly StockpileApiFactory _factory;

    /// <summary>Shares the one API host (one migrate + seed of Stockpile_Test) across tests.</summary>
    public ErrorEnvelopeApiTests(StockpileApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Unhandled_exception_answers_500_with_the_internal_error_envelope()
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "_test/unhandled");
        request.Headers.Add("X-Correlation-Id", "trace-0000-abc");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("trace-0000-abc", response.Headers.GetValues("X-Correlation-Id").Single());

        var envelope = await response.Content.ReadFromJsonAsync<ErrorEnvelope>();
        Assert.NotNull(envelope);
        Assert.Equal(ErrorCodes.InternalError, envelope!.Code);
        Assert.Equal("trace-0000-abc", envelope.CorrelationId);
        Assert.StartsWith("SUP-", envelope.SupportReference, StringComparison.Ordinal);
        Assert.True(envelope.Retryable);
        Assert.Null(envelope.FieldErrors);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$", envelope.Timestamp);

        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("InvalidOperationException", body, StringComparison.Ordinal);
        Assert.DoesNotContain("stackTrace", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".cs:line", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Business_failure_answers_the_status_registered_for_its_code()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("_test/business");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var envelope = await response.Content.ReadFromJsonAsync<ErrorEnvelope>();
        Assert.NotNull(envelope);
        Assert.Equal(ErrorCodes.QuoteAlreadyAccepted, envelope!.Code);
        Assert.Equal(
            ErrorCodes.Resolve(ErrorCodes.QuoteAlreadyAccepted)!.DefaultMessage,
            envelope.Message);
        Assert.False(envelope.Retryable);
    }

    [Fact]
    public async Task Semantic_validation_answers_422_with_camel_cased_field_errors()
    {
        using var client = AntiforgeryTestSupport.HttpsClient(_factory);
        string token = await AntiforgeryTestSupport.AcquireTokenAsync(client);
        using var request = new HttpRequestMessage(HttpMethod.Post, "_test/echo")
        {
            Content = JsonContent.Create(new { name = "" }),
        };

        using var response = await AntiforgeryTestSupport.SendUnsafeAsync(client, request, token);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var envelope = await response.Content.ReadFromJsonAsync<ErrorEnvelope>();
        Assert.NotNull(envelope);
        Assert.Equal(ErrorCodes.ValidationFailed, envelope!.Code);

        var field = Assert.Single(envelope.FieldErrors!);
        Assert.Equal("name", field.Field);
        Assert.Equal("NotEmpty", field.Code);
        Assert.False(string.IsNullOrWhiteSpace(field.Message));
    }

    [Fact]
    public async Task Wrong_typed_binding_answers_400_malformed_with_field_errors()
    {
        using var client = AntiforgeryTestSupport.HttpsClient(_factory);
        string token = await AntiforgeryTestSupport.AcquireTokenAsync(client);
        using var content = new StringContent("{\"name\":123}", Encoding.UTF8, JsonBody);
        using var request = new HttpRequestMessage(HttpMethod.Post, "_test/echo") { Content = content };

        using var response = await AntiforgeryTestSupport.SendUnsafeAsync(client, request, token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var envelope = await response.Content.ReadFromJsonAsync<ErrorEnvelope>();
        Assert.NotNull(envelope);
        Assert.Equal(ErrorCodes.MalformedRequest, envelope!.Code);

        Assert.NotEmpty(envelope.FieldErrors!);
        Assert.Contains(envelope.FieldErrors!, field => field.Field == "name" && field.Code == "Invalid");
        Assert.All(envelope.FieldErrors!, field => Assert.Equal("Invalid", field.Code));
    }

    [Fact]
    public async Task Syntax_broken_json_answers_400_malformed_envelope()
    {
        using var client = AntiforgeryTestSupport.HttpsClient(_factory);
        string token = await AntiforgeryTestSupport.AcquireTokenAsync(client);
        using var content = new StringContent("{\"name\":", Encoding.UTF8, JsonBody);
        using var request = new HttpRequestMessage(HttpMethod.Post, "_test/echo") { Content = content };

        using var response = await AntiforgeryTestSupport.SendUnsafeAsync(client, request, token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var envelope = await response.Content.ReadFromJsonAsync<ErrorEnvelope>();
        Assert.NotNull(envelope);
        Assert.Equal(ErrorCodes.MalformedRequest, envelope!.Code);
        Assert.NotNull(envelope.CorrelationId);
    }

    [Fact]
    public async Task Valid_request_passes_through_untouched()
    {
        using var client = AntiforgeryTestSupport.HttpsClient(_factory);
        string token = await AntiforgeryTestSupport.AcquireTokenAsync(client);
        using var request = new HttpRequestMessage(HttpMethod.Post, "_test/echo")
        {
            Content = JsonContent.Create(new { name = "Ok" }),
        };

        using var response = await AntiforgeryTestSupport.SendUnsafeAsync(client, request, token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var echoed = await response.Content.ReadFromJsonAsync<TestOnlyRequest>();
        Assert.Equal("Ok", echoed!.Name);
    }
}
