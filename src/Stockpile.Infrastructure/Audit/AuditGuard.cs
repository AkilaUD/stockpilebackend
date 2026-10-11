using Stockpile.Application.Audit;

namespace Stockpile.Infrastructure.Audit;

/// <summary>
/// Writes the <see cref="Guard"/> helpers for audit/security rows (column
/// length contracts from the EF configuration, enforced at the write boundary
/// so a descendant value becomes a business error, never a storage error).
/// </summary>
internal static class Guard
{
    public static void Require(string value, string column, int maxLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        if (value.Length > maxLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value), $"{column} exceeds nvarchar({maxLength}).");
        }
    }
}
