using System.Globalization;

namespace Waymark.Data.Sqlite;

/// <summary>Fixed-width UTC text timestamps, so comparing the text in SQL matches time order.</summary>
internal static class Timestamp
{
    private const string Pattern = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    public static string Format(DateTimeOffset value) =>
        value.UtcDateTime.ToString(Pattern, CultureInfo.InvariantCulture);

    public static string? FormatNullable(DateTimeOffset? value) =>
        value is null ? null : Format(value.Value);

    public static DateTimeOffset Parse(string value) =>
        DateTimeOffset.ParseExact(
            value,
            Pattern,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    public static DateTimeOffset? ParseNullable(string? value) =>
        value is null ? null : Parse(value);
}
