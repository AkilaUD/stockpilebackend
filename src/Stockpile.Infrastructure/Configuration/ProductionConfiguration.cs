using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Stockpile.Infrastructure.Configuration;

/// <summary>
/// Task 1.32 — the Production configuration gate (security-test-matrix H1-H3,
/// threat-model §11, architecture §15). In Production the host refuses to boot
/// while a required secret key is missing (H2) or still holds a placeholder
/// value (H1), and while automatic database migration is requested (H3).
/// Every other environment is untouched: Development and QA supply their own
/// values (user-secrets, settings files) and the test host its variables.
/// </summary>
public static class ProductionConfiguration
{
    /// <summary>Key whose value must never be true in Production.</summary>
    public const string AutoMigrateKey = "Database:AutoMigrate";

    /// <summary>
    /// The required secret keys Production must supply — the explicit manifest
    /// architecture §15 mandates. The connection string comes first and is the
    /// only secret existent today; JWT signing material (phase 1D) and
    /// data-protection key retention (1.33) join the manifest as their
    /// subsystems land. A missing key refuses boot (H2).
    /// </summary>
    public static readonly IReadOnlyList<string> RequiredSecretKeys =
        ["ConnectionStrings:Stockpile"];

    /// <summary>
    /// Whole-value placeholder markers (threat-model §11: "", "CHANGE_ME",
    /// "localhost") plus unambiguous variants. Matched case-insensitively on
    /// the trimmed value; a real secret (a SQL Server connection string, a
    /// signed key reference) never equals one of these — so the check is
    /// precise by construction. Value not keyed: the manifest decides which
    /// keys are secrets.
    /// </summary>
    private static readonly HashSet<string> PlaceholderValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "changeme",
        "change-me",
        "change_me",
        "change me",
        "changeme123",
        "replaceme",
        "replace-me",
        "replace_me",
        "replace me",
        "placeholder",
        "your-secret-here",
        "your-password-here",
        "secret-to-change",
        "setme",
        "set me",
        "todo",
        "localhost",
        "xxx",
    };

    /// <summary>
    /// Template-wrapped placeholders (e.g. <c>&lt;your-key-here&gt;</c>,
    /// <c>__PLACEHOLDER__</c>) — an unambiguous template shape no real value
    /// uses. Guarded by a trailing <c>$</c> so the match is whole-value.
    /// </summary>
    private static readonly Regex BracketPlaceholder = new(
        @"^<.+>$|^\[.+\]$|^__.+__$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Whether a configured value is still an unfilled placeholder.</summary>
    public static bool IsPlaceholder(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        string normalized = value.Trim();
        return PlaceholderValues.Contains(normalized) || BracketPlaceholder.IsMatch(normalized);
    }

    /// <summary>
    /// Enforces the production configuration contract. A no-op outside
    /// Production. Throws <see cref="InvalidOperationException"/> — the host
    /// boots, or it does not — with a precise, actionable message when a
    /// required secret is missing (H2), is still a placeholder (H1), or when
    /// automatic migration is requested (H3).
    /// </summary>
    public static void Validate(IConfiguration configuration, string environmentName)
    {
        if (!string.Equals(environmentName, Environments.Production, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        foreach (string key in RequiredSecretKeys)
        {
            string? value = configuration[key];

            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException(
                    $"'{key}' is required in Production but is not configured. Supply it through the "
                    + "environment variable or the secret store referenced by the SystemSetting instance, "
                    + "never a committed file (security-test-matrix H2, threat-model §11).");
            }

            if (IsPlaceholder(value))
            {
                throw new InvalidOperationException(
                    $"'{key}' still holds a placeholder value ('{value}') in Production — replace it with "
                    + "the real setting before boot (security-test-matrix H1, threat-model §11).");
            }
        }

        // H3 (§63, architecture §9.2): migrations are the deployment pipeline's
        // job in Production; the flag is a Development convenience only.
        string? autoMigrateSetting = configuration[AutoMigrateKey];
        if (bool.TryParse(autoMigrateSetting, out bool autoMigrate) && autoMigrate)
        {
            throw new InvalidOperationException(
                $"'{AutoMigrateKey}' must never be true in Production — migrations are applied by the "
                + "deployment pipeline (architecture §9.2, security-test-matrix H3).");
        }
    }
}
