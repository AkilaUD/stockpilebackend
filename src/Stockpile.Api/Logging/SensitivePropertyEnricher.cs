using Serilog.Core;
using Serilog.Events;

namespace Stockpile.Api.Logging;

/// <summary>
/// Task 1.23 — log redaction safety net (security matrix Z4).
/// Replaces the value of any log property whose <b>name</b> marks it sensitive
/// (password, token, secret, authorization, cookie, credential, …) with a fixed
/// marker before the event reaches a sink. The scan recurses into destructured
/// structures, sequences, and dictionaries, so an accidental
/// <c>Log.Information("Failed {@Attempt}", attempt)</c> cannot leak a reset
/// secret either.
/// </summary>
/// <remarks>
/// Redaction is by property name only — Serilog cannot recognise a password by
/// shape — which is why the fragment list favours false positives over misses
/// (threat-model §14: audit/security logs never contain passwords, tokens or
/// reset secrets; the same rule applies to application logs, because they are
/// shipped to the same sink). Registered after <c>FromLogContext()</c> so
/// scope-pushed properties (correlation id, request enrichment) are visible.
/// </remarks>
public sealed class SensitivePropertyEnricher : ILogEventEnricher
{
    private const string RedactedMarker = "[REDACTED]";

    /// <summary>Guard against pathological object graphs.</summary>
    private const int MaxDepth = 4;

    private static readonly string[] SensitiveFragments =
    [
        "password",
        "passphrase",
        "pwd",
        "token",
        "secret",
        "authorization",
        "cookie",
        "credential",
        "apikey",
        "api_key",
        "api-key",
    ];

    /// <inheritdoc />
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        // Collect first: AddOrUpdateProperty mutates the dictionary being read.
        var updates = new List<LogEventProperty>(logEvent.Properties.Count);

        foreach (var (name, value) in logEvent.Properties)
        {
            if (IsSensitiveName(name))
            {
                updates.Add(new LogEventProperty(name, new ScalarValue(RedactedMarker)));
                continue;
            }

            var scrubbed = ScrubValue(value, 0);
            if (!ReferenceEquals(scrubbed, value))
            {
                updates.Add(new LogEventProperty(name, scrubbed));
            }
        }

        foreach (var update in updates)
        {
            logEvent.AddOrUpdateProperty(update);
        }
    }

    private static bool IsSensitiveName(string name) =>
        !string.IsNullOrEmpty(name)
        && Array.Exists(SensitiveFragments, fragment => name.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Returns the value unchanged when nothing nested was redacted, so callers
    /// can compare by reference and skip the property update.
    /// </summary>
    private static LogEventPropertyValue ScrubValue(LogEventPropertyValue value, int depth)
    {
        if (depth > MaxDepth)
        {
            return value;
        }

        switch (value)
        {
            case StructureValue structure:
                {
                    var changed = false;
                    var rebuilt = new List<LogEventProperty>(structure.Properties.Count);

                    foreach (var property in structure.Properties)
                    {
                        if (IsSensitiveName(property.Name))
                        {
                            rebuilt.Add(new LogEventProperty(property.Name, new ScalarValue(RedactedMarker)));
                            changed = true;
                            continue;
                        }

                        var inner = ScrubValue(property.Value, depth + 1);
                        changed |= !ReferenceEquals(inner, property.Value);
                        rebuilt.Add(new LogEventProperty(property.Name, inner));
                    }

                    return changed ? new StructureValue(rebuilt, structure.TypeTag) : structure;
                }

            case SequenceValue sequence:
                {
                    var changed = false;
                    var rebuilt = new List<LogEventPropertyValue>(sequence.Elements.Count);

                    foreach (var element in sequence.Elements)
                    {
                        var inner = ScrubValue(element, depth + 1);
                        changed |= !ReferenceEquals(inner, element);
                        rebuilt.Add(inner);
                    }

                    return changed ? new SequenceValue(rebuilt) : sequence;
                }

            case DictionaryValue dictionary:
                {
                    var changed = false;
                    var rebuilt = new List<KeyValuePair<ScalarValue, LogEventPropertyValue>>(dictionary.Elements.Count);

                    foreach (var pair in dictionary.Elements)
                    {
                        var keyName = pair.Key.Value as string;

                        if (keyName is not null && IsSensitiveName(keyName))
                        {
                            rebuilt.Add(new(pair.Key, new ScalarValue(RedactedMarker)));
                            changed = true;
                            continue;
                        }

                        var inner = ScrubValue(pair.Value, depth + 1);
                        changed |= !ReferenceEquals(inner, pair.Value);
                        rebuilt.Add(new(pair.Key, inner));
                    }

                    return changed ? new DictionaryValue(rebuilt) : dictionary;
                }

            default:
                return value;
        }
    }
}
