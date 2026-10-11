using System.Text.Json;
using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Stockpile.Api.Middleware;
using Stockpile.Application.Common.Errors;
using Stockpile.Contracts.Errors;

namespace Stockpile.Api.Errors;

/// <summary>
/// Task 1.19 — the single place an exception becomes a response: the api.md §1.4
/// envelope with the status registered for its code, never a stack trace.
/// Expected failures (<see cref="AppException"/>) log at Information with their
/// support reference, malformed requests at Warning with parse detail, and
/// anything unhandled at Error with the exception — the support reference in
/// every line lets support find the stack trace in the logs (§1.5).
/// </summary>
internal sealed partial class ApiExceptionHandler(
    ILogger<ApiExceptionHandler> logger,
    IOptions<Microsoft.AspNetCore.Mvc.JsonOptions> jsonOptions) : IExceptionHandler
{
    /// <inheritdoc />
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        // The client hung up mid-request; there is nobody left to answer. 499
        // (nginx's "client closed request") keeps the completion log honest and
        // avoids the middleware's misread "produced a 404" warning for a
        // response that was deliberately left unwritten.
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            httpContext.Response.StatusCode = 499;
            return true;
        }

        var correlationId = CorrelationIdMiddleware.CorrelationIdOf(httpContext);
        ErrorEnvelope envelope;
        int statusCode;

        switch (exception)
        {
            case AppException app:
                {
                    var definition = ErrorCodes.Resolve(app.Code);
                    statusCode = definition?.HttpStatusCode ?? StatusCodes.Status500InternalServerError;
                    envelope = ErrorEnvelopeFactory.Create(
                        httpContext,
                        app.Code,
                        message: string.IsNullOrWhiteSpace(app.Message) ? null : app.Message,
                        fieldErrors: app.FieldErrors);
                    LogExpectedFailure(logger, app.Code, statusCode, correlationId, envelope.SupportReference);
                    break;
                }

            case BadHttpRequestException malformed:
                {
                    statusCode = StatusCodes.Status400BadRequest;
                    envelope = ErrorEnvelopeFactory.Create(httpContext, ErrorCodes.MalformedRequest);
                    LogMalformedRequest(logger, malformed.Message, correlationId, envelope.SupportReference);
                    break;
                }

            case ValidationException validation:
                {
                    // Safety net: the action filter normally answers 422 before an
                    // action throws this; validator-internal throws land here instead.
                    statusCode = StatusCodes.Status422UnprocessableEntity;
                    var failures = validation.Errors
                        .Select(error => new FieldError(
                            ErrorEnvelopeFactory.WireFieldName(error.PropertyName),
                            ErrorEnvelopeFactory.WireFieldCode(error.ErrorCode),
                            error.ErrorMessage))
                        .ToList();
                    envelope = ErrorEnvelopeFactory.Create(
                        httpContext, ErrorCodes.ValidationFailed, fieldErrors: failures);
                    LogValidationRejected(logger, failures.Count, correlationId, envelope.SupportReference);
                    break;
                }

            default:
                {
                    statusCode = StatusCodes.Status500InternalServerError;
                    envelope = ErrorEnvelopeFactory.Create(httpContext, ErrorCodes.InternalError);
                    LogUnhandledException(logger, exception, correlationId, envelope.SupportReference);
                    break;
                }
        }

        if (httpContext.Response.HasStarted)
        {
            return true;
        }

        // The exception path resets the response (headers included) before the
        // envelope is written — restore the id so it is on the wire as well as
        // inside the body (api.md §1.4).
        httpContext.Response.Headers[CorrelationIdMiddleware.HeaderName] = correlationId;

        // api.md §1.5: a 401 carries a scheme hint. Cookie auth's challenge sets
        // the real WWW-Authenticate header once auth lands in phase 1D.
        httpContext.Response.StatusCode = statusCode;
        httpContext.Response.ContentType = "application/json; charset=utf-8";
        await httpContext.Response.WriteAsync(
            JsonSerializer.Serialize(envelope, jsonOptions.Value.JsonSerializerOptions),
            cancellationToken);
        return true;
    }

    [LoggerMessage(
        EventId = 1900,
        Level = LogLevel.Information,
        Message = "Expected failure {ErrorCode} -> {StatusCode}. CorrelationId: {CorrelationId}, SupportReference: {SupportReference}")]
    private static partial void LogExpectedFailure(
        ILogger logger, string errorCode, int statusCode, string correlationId, string supportReference);

    [LoggerMessage(
        EventId = 1901,
        Level = LogLevel.Warning,
        Message = "Malformed request: {Detail}. CorrelationId: {CorrelationId}, SupportReference: {SupportReference}")]
    private static partial void LogMalformedRequest(
        ILogger logger, string detail, string correlationId, string supportReference);

    [LoggerMessage(
        EventId = 1902,
        Level = LogLevel.Information,
        Message = "Validation rejected the request ({FieldCount} field(s)). CorrelationId: {CorrelationId}, SupportReference: {SupportReference}")]
    private static partial void LogValidationRejected(
        ILogger logger, int fieldCount, string correlationId, string supportReference);

    [LoggerMessage(
        EventId = 1903,
        Level = LogLevel.Error,
        Message = "Unhandled exception. CorrelationId: {CorrelationId}, SupportReference: {SupportReference}")]
    private static partial void LogUnhandledException(
        ILogger logger, Exception exception, string correlationId, string supportReference);
}
