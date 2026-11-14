using System.Text.Json;
using Microsoft.Extensions.Logging;
using Waymark.Core.Approvals;
using Waymark.Core.Settings;

namespace Waymark.Data.Sqlite;

/// <summary>
/// First-run and every-start preparation: create the data folder, migrate the schema, make sure this installation
/// has an instance id, and expire approvals that were still pending when the server last stopped.
/// Call it once, before the web listeners start.
/// </summary>
public sealed class DatabaseInitializer(
    WaymarkDataOptions options,
    Migrator migrator,
    IConfigStore config,
    IApprovalStore approvals,
    TimeProvider time,
    ILogger<DatabaseInitializer> logger)
{
    private const string SystemActor = "system";

    public async Task InitializeAsync(CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(options.DatabasePath)
            ?? throw new InvalidOperationException("The database path has no directory.");
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
        {
            KeepPrivate(directory);
        }

        var schemaVersion = await migrator.MigrateAsync(ct);
        if (!OperatingSystem.IsWindows())
        {
            KeepDatabaseFilesPrivate(options.DatabasePath);
        }

        var instanceId = JsonSerializer.Serialize(Guid.NewGuid().ToString("N"));
        var createdInstanceId = await config.TryAddAsync(SettingScope.Global, SettingKeys.InstanceId, instanceId, SystemActor, ct);

        var abandoned = await approvals.ExpireAllPendingAsync(time.GetUtcNow(), "server_restart", ct);

        logger.LogInformation(
            "Database ready at {Path} (schema {Version}, new instance id: {NewInstance}, approvals expired at start: {Expired})",
            options.DatabasePath,
            schemaVersion,
            createdInstanceId,
            abandoned);
    }

    // The database, the vault and the command output belong to this user alone. Windows gets that from the per-user profile folder;
    // on macOS and Linux the folder itself is set to rwx------ (Application Support is readable by others by default).
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private void KeepPrivate(string directory)
    {
        const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        try
        {
            if (File.GetUnixFileMode(directory) != OwnerOnly)
            {
                File.SetUnixFileMode(directory, OwnerOnly);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "The data folder {Path} could not be made private to this user.", directory);
        }
    }

    // A second layer behind the rwx------ folder: the database files themselves are rw------- too.
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private void KeepDatabaseFilesPrivate(string databasePath)
    {
        foreach (var file in DatabaseFileModes.FilesOf(databasePath))
        {
            try
            {
                DatabaseFileModes.MakeOwnerOnly(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "The database file {Path} could not be made private to this user.", file);
            }
        }
    }
}
