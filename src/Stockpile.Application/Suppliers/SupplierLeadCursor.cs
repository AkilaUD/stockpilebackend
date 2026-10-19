using System.Text;

namespace Stockpile.Application.Suppliers;

/// <summary>
/// The inbox feed's cursor (api.md §1): opaque to clients, carrying the keyset
/// position of the last row handed out — the RFQ's arrival time and the lead's
/// own public id, which is exactly the feed's sort order. It is an encoding,
/// never a capability: the supplier id is resolved from the session on every
/// request, so a forged cursor can only address rows the caller already owns.
/// </summary>
public static class SupplierLeadCursor
{
    /// <summary>Encodes <c>(sortValue, leadId)</c> as base64url (api.md §1).</summary>
    public static string Encode(DateTime sortValue, Guid leadId) =>
        Base64Url(Encoding.UTF8.GetBytes(
            $"{sortValue.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture)}:{leadId:N}"));

    /// <summary>Decodes a cursor the client echoed back; false for anything malformed (the caller answers 422).</summary>
    public static bool TryDecode(string cursor, out SupplierLeadCursorPosition position)
    {
        position = null!;

        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(FromBase64Url(cursor.Trim()));
        }
        catch (FormatException)
        {
            return false;
        }

        int separator = decoded.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0
            || !long.TryParse(
                decoded[..separator], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out long ticks)
            || !Guid.TryParseExact(decoded[(separator + 1)..], "N", out Guid leadId)
            || ticks < DateTime.MinValue.Ticks
            || ticks > DateTime.MaxValue.Ticks)
        {
            return false;
        }

        // The columns are written in UTC (domain-model §1); the tick value is the
        // whole position and the kind is reconstructed, never transported.
        position = new SupplierLeadCursorPosition(new DateTime(ticks, DateTimeKind.Utc), leadId);
        return true;
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes)
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        string padded = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight((padded.Length + 3) / 4 * 4, '='));
    }
}

/// <summary>A decoded cursor: the keyset position of the last row of the previous page.</summary>
public sealed record SupplierLeadCursorPosition(DateTime SortValue, Guid LeadId);
