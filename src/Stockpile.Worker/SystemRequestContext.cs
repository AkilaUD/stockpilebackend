using Stockpile.Application.Audit;

namespace Stockpile.Worker;

/// <summary>
/// <see cref="ICurrentRequestContext"/> for the Worker (task 1.29): outbox
/// consumers run outside any HTTP request, so there is no actor, address, or
/// correlation unless a consumer supplies it explicitly.
/// </summary>
public sealed class SystemRequestContext : ICurrentRequestContext
{
    /// <inheritdoc />
    public Guid? ActorId => null;

    /// <inheritdoc />
    public string? IpAddress => null;

    /// <inheritdoc />
    public string? UserAgent => null;

    /// <inheritdoc />
    public string? CorrelationId => null;
}
