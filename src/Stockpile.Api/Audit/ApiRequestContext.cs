using Microsoft.AspNetCore.Http;
using Stockpile.Api.Middleware;
using Stockpile.Application.Audit;

namespace Stockpile.Api.Audit;

/// <summary>
/// <see cref="ICurrentRequestContext"/> from the live <c>HttpContext</c>
/// (task 1.29): actor from the <c>sub</c> claim, client address, user agent,
/// and the correlation id assigned by <see cref="CorrelationIdMiddleware"/>.
/// </summary>
public sealed class ApiRequestContext(IHttpContextAccessor accessor) : ICurrentRequestContext
{
    /// <inheritdoc />
    public Guid? ActorId
    {
        get
        {
            if (accessor.HttpContext?.User.FindFirst("sub") is { } subject
                && Guid.TryParse(subject.Value, out Guid subjectId))
            {
                return subjectId;
            }

            return null;
        }
    }

    /// <inheritdoc />
    public string? IpAddress => accessor.HttpContext?.Connection.RemoteIpAddress?.ToString();

    /// <inheritdoc />
    public string? UserAgent => accessor.HttpContext?.Request.Headers.UserAgent.ToString();

    /// <inheritdoc />
    public string? CorrelationId => accessor.HttpContext is { } context
        ? CorrelationIdMiddleware.CorrelationIdOf(context)
        : null;
}
