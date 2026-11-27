using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Waymark.Data.Sqlite;

/// <summary>
/// Applies the embedded numbered scripts (001_name.sql, 002_name.sql, ...) in order, one transaction per script,
/// tracking progress in PRAGMA user_version. Refuses to run against a database from a newer build.
/// Before it changes an existing database it writes a consistent copy of it to the backups folder beside it
/// (SQLite's online backup), keeps the newest few, and does not migrate when that copy fails.
/// </summary>
public sealed class Migrator(ISqliteConnectionFactory connections, ILogger<Migrator> logger)
{
    private const string ResourcePrefix = "Waymark.Data.Migrations.";

    /// <summary>The folder beside the database that holds the copies made before migrations.</summary>
    public const string BackupFolderName = "backups";

    /// <summary>How many copies made before migrations are kept; older ones are deleted.</summary>
    public const int BackupsKept = 5;

    private const string BackupPrefix = "waymark-v";

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

        var pending = scripts.Where(s => s.Version > current).ToList();
        if (pending.Count > 0 && current > 0)
        {
            BackUp(connection, current, latest);
        }

        foreach (var script in pending)
        {
            await using var transaction = connection.BeginTransaction();
            await ExecuteAsync(connection, transaction, script.Sql, ct);
            await ExecuteAsync(connection, transaction, string.Create(CultureInfo.InvariantCulture, $"PRAGMA user_version = {script.Version};"), ct);
            await transaction.CommitAsync(ct);
            logger.LogInformation("Applied database migration {Migration}", script.Name);
        }

        return latest;
    }

    // A consistent copy while other connections may write (WAL): SQLite's online backup, not a file copy. A database that is not a file
    // (in memory) has nothing to protect. Any failure stops the migration, so no schema change ever runs without a copy to go back to.
    private void BackUp(SqliteConnection source, int fromVersion, int toVersion)
    {
        var databasePath = source.DataSource;
        if (string.IsNullOrWhiteSpace(databasePath) || databasePath == ":memory:" || !File.Exists(databasePath))
        {
            return;
        }

        var folder = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(databasePath)) ?? ".", BackupFolderName);
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var target = Path.Combine(folder, string.Create(CultureInfo.InvariantCulture, $"{BackupPrefix}{fromVersion}-before-v{toVersion}-{stamp}.db"));
        try
        {
            Directory.CreateDirectory(folder);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            var builder = new SqliteConnectionStringBuilder { DataSource = target, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false };
            using (var destination = new SqliteConnection(builder.ToString()))
            {
                destination.Open();
                source.BackupDatabase(destination);
            }

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"The database could not be backed up to '{target}' before migrating it from schema {fromVersion} to {toVersion}, so it was left unchanged. " +
                "Free disk space or fix the folder's permissions, then start Waymark again.",
                ex);
        }

        logger.LogInformation("Backed up the database to {Backup} before migrating schema {From} to {To}", target, fromVersion, toVersion);
        PruneBackups(folder);
    }

    // The newest copies stay; a copy that cannot be deleted is left for the owner and does not stop the start.
    private void PruneBackups(string folder)
    {
        var old = new DirectoryInfo(folder)
            .GetFiles(BackupPrefix + "*-before-v*.db")
            .OrderByDescending(f => f.CreationTimeUtc)
            .ThenByDescending(f => f.Name, StringComparer.Ordinal)
            .Skip(BackupsKept);
        foreach (var file in old)
        {
            try
            {
                file.Delete();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "An old database backup could not be deleted: {Backup}", file.FullName);
            }
        }
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
