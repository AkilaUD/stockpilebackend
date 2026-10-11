namespace Stockpile.Application.Audit;

/// <summary>
/// Ambient request facts for audit/security rows, sourced from whatever host
/// context exists (task 1.29). The API resolves them from the live
/// <c>HttpContext</c>; the Worker returns the system defaults (no actor, no
/// address). Hosts register their implementation; the audit services never
/// depend on ASP.NET specifics.
/// </summary>
public interface ICurrentRequestContext
{
    /// <summary>Acting user's public GUID; null = system.</summary>
    Guid? ActorId { get; }

    string? IpAddress { get; }

    string? UserAgent { get; }

    /// <summary>Correlation id of the originating request (api.md §1.4).</summary>
    string? CorrelationId { get; }
}
