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

        var schemaVersion = await migrator.MigrateAsync(ct);

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
}
