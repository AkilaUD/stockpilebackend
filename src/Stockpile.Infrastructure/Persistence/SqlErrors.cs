using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Stockpile.Infrastructure.Persistence;

/// <summary>
/// Narrow classification of SQL Server failures the stores translate into
/// business conflicts. Anything not recognised here must still propagate as a
/// 500 — a store never swallows an error it cannot name.
/// </summary>
internal static class SqlErrors
{
    private const int UniqueIndexViolation = 2601;
    private const int UniqueConstraintViolation = 2627;

    /// <summary>True when the failure is a unique index/constraint violation.</summary>
    public static bool IsUniqueViolation(DbUpdateException exception)
        => FindSql(exception) is { Number: UniqueIndexViolation or UniqueConstraintViolation };

    /// <summary>True when the unique violation names <paramref name="indexOrTable"/> (the engine puts the object name in the message).</summary>
    public static bool IsUniqueViolationOn(DbUpdateException exception, string indexOrTable)
        => FindSql(exception) is { Number: UniqueIndexViolation or UniqueConstraintViolation } sql
            && sql.Message.Contains(indexOrTable, StringComparison.OrdinalIgnoreCase);

    private static SqlException? FindSql(Exception? exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqlException sql)
            {
                return sql;
            }
        }

        return null;
    }
}
