using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Stockpile.Infrastructure.Persistence;

/// <summary>
/// Shared model conventions (architecture §9.1): enums are stored as required
/// <c>nvarchar(40)</c> member names with a matching CHECK constraint. The check
/// SQL is generated from the enum itself, so the database can never drift from
/// the CLR type (domain-model §1 "C# enum → nvarchar(40) + CHECK constraint").
/// </summary>
public static class ModelBuilderExtensions
{
    /// <summary>Enum property → required nvarchar(40) holding the member name.</summary>
    public static PropertyBuilder<TEnum> HasEnumString<TEnum>(this PropertyBuilder<TEnum> builder)
        where TEnum : struct, Enum
        => builder
            .HasConversion<EnumToStringConverter<TEnum>>()
            .HasMaxLength(40)
            .IsRequired();

    /// <summary>SQL literal list of member names, e.g. <c>'Active', 'Suspended', 'Deleted'</c>.</summary>
    public static string EnumValuesSql<TEnum>()
        where TEnum : struct, Enum
        => string.Join(", ", Enum.GetNames<TEnum>().Select(static n => $"'{n}'"));

    /// <summary>Full IN expression for an enum column: <c>[Column] IN ('A', 'B', …)</c>.</summary>
    public static string EnumIn<TEnum>(string columnName)
        where TEnum : struct, Enum
        => $"[{columnName}] IN ({EnumValuesSql<TEnum>()})";
}
