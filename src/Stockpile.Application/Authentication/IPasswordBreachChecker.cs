namespace Stockpile.Application.Authentication;

/// <summary>
/// Threat-model §4 hook — whether a password is already publicly breached
/// (e.g. a HIBP-style corpus, checked with k-anonymity so the full password
/// never leaves the platform). The hook is a port from 1.36; it is only
/// consulted when <c>PasswordPolicy:BreachCheckEnabled</c> is true, and the
/// host refuses to boot with that flag set but no implementation registered —
/// the platform never claims breach checking it does not have. The concrete
/// implementation arrives with the platform operations phase.
/// </summary>
public interface IPasswordBreachChecker
{
    /// <summary>True when the password appears in a known-breach corpus.</summary>
    Task<bool> IsBreachedAsync(string password, CancellationToken cancellationToken);
}