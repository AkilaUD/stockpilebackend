using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Stockpile.Api.Errors;
using Stockpile.Api.Middleware;
using Stockpile.Contracts.Errors;

namespace Stockpile.Api.Filters;

/// <summary>
/// Task 1.21 — runs any registered <c>IValidator&lt;T&gt;</c> for every action
/// argument before the action executes (validators live in Application; spec
/// §8 "Application layer owns … validation orchestration"). Failures answer
/// 422 with the api.md §1.4 envelope and fieldErrors. Model-binding failures
/// (bad GUID, wrong JSON type) are separate: <c>InvalidModelStateResponseFactory</c>
/// answers those with 400 MALFORMED_REQUEST.
/// </summary>
internal sealed class ValidationActionFilter : IAsyncActionFilter
{
    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var services = context.HttpContext.RequestServices;

        foreach (var (parameterName, value) in context.ActionArguments)
        {
            if (value is null)
            {
                continue;
            }

            if (services.GetService(typeof(IValidator<>).MakeGenericType(value.GetType())) is not IValidator validator)
            {
                continue;
            }

            var contextType = typeof(ValidationContext<>).MakeGenericType(value.GetType());
            var validationContext = (IValidationContext)Activator.CreateInstance(contextType, value)!;
            var result = await validator.ValidateAsync(validationContext, context.HttpContext.RequestAborted);

            if (result.IsValid)
            {
                continue;
            }

            var fieldErrors = result.Errors
                .Select(error => new FieldError(
                    ErrorEnvelopeFactory.WireFieldName(
                        string.IsNullOrEmpty(error.PropertyName) ? parameterName : error.PropertyName),
                    ErrorEnvelopeFactory.WireFieldCode(error.ErrorCode),
                    error.ErrorMessage))
                .ToList();

            var envelope = ErrorEnvelopeFactory.Create(
                context.HttpContext, ErrorCodes.ValidationFailed, fieldErrors: fieldErrors);

            context.Result = new ObjectResult(envelope)
            {
                StatusCode = StatusCodes.Status422UnprocessableEntity,
            };
            return;
        }

        await next();
    }
}
