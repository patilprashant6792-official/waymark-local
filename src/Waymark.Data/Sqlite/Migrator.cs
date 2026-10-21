using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Waymark.Data.Sqlite;

/// <summary>
/// Applies the embedded numbered scripts (001_name.sql, 002_name.sql, ...) in order, one transaction per script,
/// tracking progress in PRAGMA user_version. Refuses to run against a database from a newer build.
/// </summary>
public sealed class Migrator(ISqliteConnectionFactory connections, ILogger<Migrator> logger)
{
    private const string ResourcePrefix = "Waymark.Data.Migrations.";

    private static readonly Version MinimumSqliteVersion = new(3, 37);

    /// <summary>Returns the schema version after migrating.</summary>
    public async Task<int> MigrateAsync(CancellationToken ct)
    {
        var scripts = LoadScripts();
        var latest = scripts.Count == 0 ? 0 : scripts[^1].Version;

        await using var connection = await connections.OpenAsync(ct);

        if (Version.TryParse(connection.ServerVersion, out var sqliteVersion) && sqliteVersion < MinimumSqliteVersion)
        {
            throw new InvalidOperationException(
                $"SQLite {connection.ServerVersion} is too old; STRICT tables need {MinimumSqliteVersion} or newer.");
        }

        await ExecuteAsync(connection, null, "PRAGMA journal_mode=WAL;", ct);

        var current = await GetUserVersionAsync(connection, ct);
        if (current > latest)
        {
            throw new InvalidOperationException(
                $"The database is at schema version {current} but this build only knows version {latest}. Upgrade Waymark.");
        }

        foreach (var script in scripts.Where(s => s.Version > current))
        {
            await using var transaction = connection.BeginTransaction();
            await ExecuteAsync(connection, transaction, script.Sql, ct);
            await ExecuteAsync(connection, transaction, string.Create(CultureInfo.InvariantCulture, $"PRAGMA user_version = {script.Version};"), ct);
            await transaction.CommitAsync(ct);
            logger.LogInformation("Applied database migration {Migration}", script.Name);
        }

        return latest;
    }

    private static List<MigrationScript> LoadScripts()
    {
        var assembly = typeof(Migrator).Assembly;
        var scripts = new List<MigrationScript>();

        foreach (var resource in assembly.GetManifestResourceNames())
        {
            if (!resource.StartsWith(ResourcePrefix, StringComparison.Ordinal) || !resource.EndsWith(".sql", StringComparison.Ordinal))
            {
                continue;
            }

            var fileName = resource[ResourcePrefix.Length..];
            var separator = fileName.IndexOf('_', StringComparison.Ordinal);
            if (separator <= 0 || !int.TryParse(fileName.AsSpan(0, separator), NumberStyles.None, CultureInfo.InvariantCulture, out var version) || version <= 0)
            {
                throw new InvalidOperationException($"Migration '{fileName}' must be named <number>_<name>.sql.");
            }

            using var stream = assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"Migration resource '{resource}' could not be opened.");
            using var reader = new StreamReader(stream, Encoding.UTF8);
            scripts.Add(new MigrationScript(version, fileName, reader.ReadToEnd()));
        }

        scripts.Sort((a, b) => a.Version.CompareTo(b.Version));
        for (var i = 0; i < scripts.Count; i++)
        {
            if (scripts[i].Version != i + 1)
            {
                throw new InvalidOperationException("Migration numbers must be contiguous and start at 1.");
            }
        }

        return scripts;
    }

    private static async Task<int> GetUserVersionAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        var value = await command.ExecuteScalarAsync(ct);
        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction? transaction, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        await command.ExecuteNonQueryAsync(ct);
    }

    private sealed record MigrationScript(int Version, string Name, string Sql);
}
