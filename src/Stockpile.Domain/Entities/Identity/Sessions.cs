using Stockpile.Domain.Common;

namespace Stockpile.Domain.Entities.Identity;

/// <summary>
/// Server-side session record (domain-model §2.5) — exists so sessions are
/// revocable now, not merely expired later. Guid v4: sensitive.
/// </summary>
public sealed class LoginSession
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }

    public DateTime IssuedAt { get; set; }

    public DateTime ExpiresAt { get; set; }

    /// <summary>Non-null once logged out or revoked (including logout-all).</summary>
    public DateTime? RevokedAt { get; set; }

    public string? IpAddress { get; set; }

    public string? UserAgent { get; set; }

    /// <summary>Sliding-heartbeat marker for session listing.</summary>
    public DateTime LastSeenAt { get; set; }
}

/// <summary>
/// Rotating refresh token (domain-model §2.5): only the SHA-256 hash is stored.
/// Replaying a rotated token revokes the whole chain (reuse detection).
/// </summary>
public sealed class RefreshSession
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Owning <see cref="LoginSession.Id"/>.</summary>
    public Guid SessionId { get; set; }

    /// <summary>SHA-256 of the opaque refresh token, nvarchar(64), unique.</summary>
    public string TokenHash { get; set; } = string.Empty;

    public DateTime ExpiresAt { get; set; }

    /// <summary>Chain link: the session created when this token was rotated.</summary>
    public Guid? RotatedToId { get; set; }

    public DateTime? RevokedAt { get; set; }
}

/// <summary>
/// One password-reset token (security-threat-model T1.5, task 1.37): only the
/// SHA-256 hash of the opaque token is stored — "hashed at rest", like
/// <see cref="RefreshSession"/> above. The row is the single-use ledger (A5):
/// completion sets <see cref="UsedAtUtc"/> and the row dies at
/// <see cref="ExpiresAtUtc"/> (Auth:PasswordResetTokenMinutes, A6); issuing a
/// new token consumes outstanding ones (one live token per user).
/// <para>
/// Implements <see cref="IHasDomainEvents"/> so the issue step can raise the
/// <c>identity.password.reset_requested</c> outbox event in the SAME
/// <c>SaveChanges</c> that persists the row (architecture §10.1): the payload
/// carries the opaque token for the transactional reset email (task 1.38).
/// </para>
/// </summary>
public sealed class PasswordResetToken : IHasDomainEvents
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Owning <see cref="User.PublicId"/>.</summary>
    public Guid UserId { get; set; }

    /// <summary>SHA-256 of the opaque reset token, nvarchar(64).</summary>
    public string TokenHash { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }

    public DateTime ExpiresAtUtc { get; set; }

    /// <summary>Non-null once used — consumed rows are facts, never deleted (X9).</summary>
    public DateTime? UsedAtUtc { get; set; }

    private readonly List<DomainEvent> _events = new();

    /// <inheritdoc />
    public IReadOnlyCollection<DomainEvent> Events => _events;

    /// <inheritdoc />
    public void ClearEvents() => _events.Clear();

    /// <summary>Attaches a domain event; the outbox interceptor flushes it at the next <c>SaveChanges</c>.</summary>
    public void Raise(DomainEvent domainEvent) => _events.Add(domainEvent);
}

/// <summary>
/// One email-verification token (task 1.38, threat-model line 169, security-test-matrix
/// A11): only the SHA-256 hash of the opaque token is stored — "hashed at rest", the
/// same ledger discipline as <see cref="PasswordResetToken"/>. Presenting it sets
/// <see cref="User.EmailConfirmedAt"/> and consumes the row (single-use); the row dies
/// at <see cref="ExpiresAtUtc"/> (Auth:EmailVerificationTokenMinutes); issuing a new
/// token consumes outstanding ones — one live token per account. The token identifies
/// its account, so the (anonymous) verify endpoint probes by hash alone.
/// <para>
/// Implements <see cref="IHasDomainEvents"/> so minting raises the
/// <c>identity.user.registered</c> outbox event in the SAME <c>SaveChanges</c> that
/// persists the row: the payload carries the opaque token for the verification email.
/// The outbox row is delivery transport — payloads are never written to any log sink
/// (threat-model T3.6).
/// </para>
/// </summary>
public sealed class EmailVerificationToken : IHasDomainEvents
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Owning <see cref="User.PublicId"/>.</summary>
    public Guid UserId { get; set; }

    /// <summary>SHA-256 of the opaque verification token, nvarchar(64), unique.</summary>
    public string TokenHash { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }

    public DateTime ExpiresAtUtc { get; set; }

    /// <summary>Non-null once used — consumed rows are facts, never deleted (X9).</summary>
    public DateTime? UsedAtUtc { get; set; }

    private readonly List<DomainEvent> _events = new();

    /// <inheritdoc />
    public IReadOnlyCollection<DomainEvent> Events => _events;

    /// <inheritdoc />
    public void ClearEvents() => _events.Clear();

    /// <summary>Attaches a domain event; the outbox interceptor flushes it at the next <c>SaveChanges</c>.</summary>
    public void Raise(DomainEvent domainEvent) => _events.Add(domainEvent);
}
