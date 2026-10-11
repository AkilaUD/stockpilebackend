using Microsoft.Extensions.Logging;

namespace Stockpile.Api.RateLimiting;

/// <summary>
/// Structured logging for the burst → SecurityEvent persistence path (1.29).
/// EventId 2201 continues the 22xx burst-monitor range (2200 = burst detected).
/// </summary>
internal static partial class BurstSecurityEventLog
{
    [LoggerMessage(
        EventId = 2201,
        Level = LogLevel.Warning,
        Message = "Failed to persist RateLimitHit security event for policy '{Policy}'")]
    public static partial void PersistFailed(ILogger logger, Exception exception, string policy);
}
