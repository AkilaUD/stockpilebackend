namespace Stockpile.Domain.Enums;

/// <summary>Outcome recorded on every audit row (domain-model §10).</summary>
public enum AuditResult
{
    Success = 0,
    Failure = 1,
    Denied = 2,
}

/// <summary>
/// Security-relevant event kinds (domain-model §10). Stored on the
/// <c>SecurityEvent</c> table, which has its own retention from <c>AuditLog</c>.
/// </summary>
public enum SecurityEventType
{
    LoginSuccess = 0,
    LoginFailure = 1,
    LockoutTriggered = 2,
    LockoutReleased = 3,
    PasswordChanged = 4,
    PasswordResetRequested = 5,
    PasswordResetCompleted = 6,
    EmailVerificationRequested = 7,
    EmailVerified = 8,
    SessionRevoked = 9,
    AllSessionsRevoked = 10,
    PrivilegeChanged = 11,
    RateLimitHit = 12,
    WebhookSignatureFailure = 13,
    RefreshReuseDetected = 14,
}

/// <summary>Outbox processing state (architecture §10.1).</summary>
public enum OutboxMessageStatus
{
    Pending = 0,
    Processed = 1,
    DeadLettered = 2,
}

/// <summary>Media lifecycle (domain-model §4.7).</summary>
public enum MediaAssetStatus
{
    Active = 0,
    Archived = 1,
    Rejected = 2,
}

/// <summary>Declared type of a <c>SystemSetting.Value</c>, so readers parse strictly (domain-model §10).</summary>
#pragma warning disable CA1720 // Members deliberately name the CLR types they parse ("integer", "decimal", "string")
public enum SettingValueType
{
    String = 0,
    Integer = 1,
    Decimal = 2,
    Boolean = 3,
    Json = 4,
    Duration = 5,
}
#pragma warning restore CA1720

/// <summary>Notification channels (architecture §11).</summary>
public enum NotificationChannel
{
    Email = 0,
    Sms = 1,
    WhatsApp = 2,
    InApp = 3,
}
