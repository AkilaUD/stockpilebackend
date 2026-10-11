using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Stockpile.Application.Common.Errors;
using Stockpile.Contracts.Errors;

namespace Stockpile.UnitTests.Errors;

/// <summary>
/// Task 1.20/1.19 — the error registry is the single source every envelope is
/// built from; these tests pin its invariants (constants ↔ registry coverage,
/// well-formed rows) and the wire shape of the envelope itself.
/// </summary>
public sealed class ErrorRegistryTests
{
    [Fact]
    public void Every_error_code_constant_has_exactly_one_registry_entry()
    {
        var constants = typeof(ErrorCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(static field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(static field => (string)field.GetValue(null)!)
            .OrderBy(static code => code, StringComparer.Ordinal);

        var registered = ErrorCodes.All
            .Select(static definition => definition.Code)
            .OrderBy(static code => code, StringComparer.Ordinal);

        // Sequence equality catches missing rows, stale rows, and duplicates.
        Assert.Equal(constants, registered);
    }

    [Fact]
    public void Every_registry_row_is_well_formed()
    {
        Assert.All(ErrorCodes.All, definition =>
        {
            Assert.Equal(definition.Code, definition.Code.ToUpperInvariant());
            Assert.DoesNotContain(definition.Code, char.IsLower);
            Assert.False(string.IsNullOrWhiteSpace(definition.DefaultMessage));
            Assert.NotEqual(definition.Code, definition.DefaultMessage);
            Assert.InRange(definition.HttpStatusCode, 400, 599);
        });
    }

    [Fact]
    public void Only_transient_failures_are_marked_retryable()
    {
        var retryable = ErrorCodes.All
            .Where(static definition => definition.Retryable)
            .Select(static definition => definition.Code)
            .OrderBy(static code => code, StringComparer.Ordinal);

        Assert.Equal(
            new[] { ErrorCodes.InternalError, ErrorCodes.RateLimited },
            retryable);
    }

    [Theory]
    [InlineData(ErrorCodes.ResourceNotFound, 404)]
    [InlineData(ErrorCodes.CrossTenantDenied, 404)]
    [InlineData(ErrorCodes.QuoteExpired, 403)]
    [InlineData(ErrorCodes.QuoteAlreadyAccepted, 409)]
    [InlineData(ErrorCodes.RateLimited, 429)]
    [InlineData(ErrorCodes.ValidationFailed, 422)]
    [InlineData(ErrorCodes.MalformedRequest, 400)]
    [InlineData(ErrorCodes.InternalError, 500)]
    public void Resolve_maps_known_codes_to_their_registered_status(string code, int status)
    {
        var definition = ErrorCodes.Resolve(code);

        Assert.NotNull(definition);
        Assert.Equal(status, definition.HttpStatusCode);
    }

    [Fact]
    public void Resolve_returns_null_for_unregistered_codes() =>
        Assert.Null(ErrorCodes.Resolve("NOT_A_REAL_CODE"));
}

/// <summary>Wire-shape contract for the api.md §1.4 envelope.</summary>
public sealed class ErrorEnvelopeTests
{
    private static readonly JsonSerializerOptions WireOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static ErrorEnvelope Envelope(IReadOnlyList<FieldError>? fieldErrors) =>
        new(
            Code: ErrorCodes.QuoteAlreadyAccepted,
            Message: "This quote has already been accepted.",
            FieldErrors: fieldErrors,
            CorrelationId: "01995f3a-7d3c-4f6e-9a1b-2c3d4e5f6a7b",
            SupportReference: "SUP-7f3a2b",
            Retryable: false,
            Timestamp: "2026-10-06T09:14:22.118Z");

    [Fact]
    public void Optional_members_are_omitted_never_null()
    {
        var json = JsonSerializer.Serialize(Envelope(null), WireOptions);

        Assert.DoesNotContain("fieldErrors", json);
        Assert.DoesNotContain("null", json);
        Assert.Contains("\"code\":\"QUOTE_ALREADY_ACCEPTED\"", json);
        Assert.Contains("\"correlationId\":\"01995f3a-7d3c-4f6e-9a1b-2c3d4e5f6a7b\"", json);
        Assert.Contains("\"supportReference\":\"SUP-7f3a2b\"", json);
        Assert.Contains("\"retryable\":false", json);
        Assert.Contains("\"timestamp\":\"2026-10-06T09:14:22.118Z\"", json);
    }

    [Fact]
    public void Field_errors_serialize_with_their_field_code_and_message()
    {
        var json = JsonSerializer.Serialize(
            Envelope([new FieldError("items[2].quantity", "OutOfRange", "Quantity must be greater than zero.")]),
            WireOptions);

        Assert.Contains(
            "\"fieldErrors\":[{\"field\":\"items[2].quantity\",\"code\":\"OutOfRange\"," +
            "\"message\":\"Quantity must be greater than zero.\"}]",
            json);
    }
}

/// <summary>The exception the application layer raises through Contracts codes.</summary>
public sealed class AppExceptionTests
{
    [Fact]
    public void Code_only_ctor_falls_back_to_the_registry_default_message()
    {
        var exception = new AppException(ErrorCodes.RfqExpired);

        Assert.Equal(ErrorCodes.RfqExpired, exception.Code);
        Assert.Equal(ErrorCodes.Resolve(ErrorCodes.RfqExpired)!.DefaultMessage, exception.Message);
        Assert.Null(exception.FieldErrors);
    }

    [Fact]
    public void Specific_message_wins_over_the_registry_default()
    {
        var exception = new AppException(ErrorCodes.RfqNoItems, "This RFQ has only closed lines.");

        Assert.Equal("This RFQ has only closed lines.", exception.Message);
    }

    [Fact]
    public void Field_errors_travel_with_the_exception()
    {
        var exception = new AppException(
            ErrorCodes.ValidationFailed,
            [new FieldError("quantity", "NotEmpty", "Quantity must be greater than zero.")]);

        var field = Assert.Single(exception.FieldErrors!);
        Assert.Equal("quantity", field.Field);
    }
}
