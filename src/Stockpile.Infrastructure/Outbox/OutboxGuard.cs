namespace Stockpile.Infrastructure.Outbox;

/// <summary>
/// Internal guards shared by the outbox pipeline. Kept separate so the
/// interceptor's schema contract (event type length) is enforced in one place.
/// </summary>
internal static class OutboxGuard
{
    public static void ValidateEventType(string eventType, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(eventType))
        {
            throw new InvalidOperationException(
                "A domain event must carry a non-empty EventType (architecture §10.2 catalogue).");
        }

        if (eventType.Length > maxLength)
        {
            throw new InvalidOperationException(
                $"EventType '{eventType}' exceeds the nvarchar({maxLength}) column limit.");
        }
    }
}
