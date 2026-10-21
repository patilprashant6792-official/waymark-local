namespace Waymark.Data.Sqlite;

/// <summary>Enums are stored as their lower-case names; CHECK constraints in the schema mirror them.</summary>
internal static class EnumText
{
    public static string ToDb<T>(T value) where T : struct, Enum => value.ToString().ToLowerInvariant();

    public static T FromDb<T>(string text) where T : struct, Enum => Enum.Parse<T>(text, ignoreCase: true);
}
