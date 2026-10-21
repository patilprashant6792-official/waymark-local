using Dapper;

namespace Waymark.Data.Sqlite;

/// <summary>One-time Dapper configuration; the static constructor makes it thread-safe.</summary>
internal static class DapperSetup
{
    static DapperSetup()
    {
        // Lets snake_case columns (client_id) map to PascalCase properties (ClientId).
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    public static void Ensure()
    {
        // Touching the type runs the static constructor.
    }
}
