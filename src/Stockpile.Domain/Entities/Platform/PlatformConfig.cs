using Stockpile.Domain.Enums;

namespace Stockpile.Domain.Entities.Platform;

/// <summary>
/// Seeded state-machine transition table (domain-model §10, architecture §6) —
/// readable by the admin UI and evaluated from an in-memory snapshot by
/// <c>StateMachine.Evaluate()</c>. Declared once, in
/// <c>Stockpile.Domain.StateMachines</c>, and mirrored here at seed time.
/// </summary>
public sealed class StateTransition
{
    /// <summary>Aggregate/state-machine name, e.g. <c>Quote</c>, <c>RFQ</c>.</summary>
    public string Entity { get; set; } = string.Empty;

    /// <summary>Enum state value, or <c>*</c> for "any state".</summary>
    public string FromState { get; set; } = string.Empty;

    /// <summary>Enum state value.</summary>
    public string ToState { get; set; } = string.Empty;

    /// <summary>Permission code evaluated by the transition; null = system-only.</summary>
    public string? PermissionCode { get; set; }

    /// <summary>A reason is mandatory when true (spec: "reason required").</summary>
    public bool RequiresReason { get; set; }

    /// <summary>Explicitly rejected edge, evaluated for a specific error (e.g. accept expired quote).</summary>
    public bool IsForbidden { get; set; }

    /// <summary>Spec's "Reason" notes, e.g. notify supplier, reserved state.</summary>
    public string? Notes { get; set; }
}

/// <summary>
/// Runtime configuration (domain-model §10): key/value with declared type.
/// Secret-flagged values are excluded from logs and any export surface.
/// </summary>
public sealed class SystemSetting
{
    /// <summary>Dotted key, e.g. <c>currency.default</c> — also the primary key.</summary>
    public string Key { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    public SettingValueType ValueType { get; set; }

    public bool IsSecret { get; set; }

    /// <summary>Public GUID of the last editor; null = system seed.</summary>
    public Guid? UpdatedBy { get; set; }
}

/// <summary>
/// Feature flag (domain-model §10): runtime-enabled by code, with rollout
/// percentage and environment allow-list.
/// </summary>
public sealed class FeatureFlag
{
    /// <summary>Flag code, e.g. <c>search.commandOverlay</c> — also the primary key.</summary>
    public string Code { get; set; } = string.Empty;

    public bool Enabled { get; set; }

    /// <summary>0–100 percentage rollout; 0 = off for everyone unless Enabled gates it.</summary>
    public int RolloutPercent { get; set; }

    /// <summary>Comma-separated environment names allowed to see the flag, e.g. <c>Development,QA</c>.</summary>
    public string AllowedEnvironments { get; set; } = string.Empty;
}
