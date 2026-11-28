using System.Text.Json;
using Dapper;
using Waymark.Core.Commands;
using Waymark.Data.Sqlite;

namespace Waymark.Data.Stores;

public sealed class TerminalStore(ISqliteConnectionFactory connections, IWriteQueue writes) : ITerminalStore
{
    private const string Columns =
        "id, workspace_id, workspace_name, name, folder, state, current_run_id, last_run_id, operation, command_line, urls_json, ports_json, pid, " +
        "pid_start_utc, program, exit_code, state_note, stopped_by, ready_how, started_utc, ready_utc, ended_utc, last_output_utc, approved_json, " +
        "launch_json, created_utc, updated_utc";

    private const string LiveStates = "'starting', 'ready', 'appdown'";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    // Columns that belong to the program now running: a new run starts them empty, otherwise a null value keeps what is stored.
    private static readonly string[] RunColumns =
    [
        "pid", "pid_start_utc", "program", "exit_code", "state_note", "stopped_by", "ready_how", "ready_utc", "ended_utc",
    ];

    public Task<Terminal> CreateAsync(NewTerminal terminal, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        return writes.ExecuteAsync<Terminal>(async (connection, tx, k) =>
        {
            var now = Timestamp.Format(terminal.CreatedUtc);
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO terminals (workspace_id, workspace_name, name, folder, state, created_utc, updated_utc) " +
                "VALUES (@workspaceId, @workspaceName, @name, @folder, 'idle', @now, @now)",
                new { workspaceId = terminal.WorkspaceId, workspaceName = terminal.WorkspaceName, name = terminal.Name, folder = terminal.Folder, now },
                tx,
                cancellationToken: k));
            var row = await connection.QuerySingleAsync<Row>(new CommandDefinition(
                "SELECT " + Columns + " FROM terminals WHERE id = last_insert_rowid()",
                transaction: tx,
                cancellationToken: k));
            return ToTerminal(row);
        }, ct);
    }

    public async Task<Terminal?> GetAsync(long id, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            "SELECT " + Columns + " FROM terminals WHERE id = @id",
            new { id },
            cancellationToken: ct));
        return row is null ? null : ToTerminal(row);
    }

    public async Task<Terminal?> GetByNameAsync(long? workspaceId, string name, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        await using var connection = await connections.OpenAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            "SELECT " + Columns + " FROM terminals WHERE name = @name AND " +
            "((@workspaceId IS NULL AND workspace_id IS NULL) OR workspace_id = @workspaceId)",
            new { workspaceId, name },
            cancellationToken: ct));
        return row is null ? null : ToTerminal(row);
    }

    public async Task<IReadOnlyList<Terminal>> ListAsync(TerminalQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using var connection = await connections.OpenAsync(ct);
        var rows = await connection.QueryAsync<Row>(new CommandDefinition(
            "SELECT " + Columns + " FROM terminals " +
            "WHERE ((@systemOnly = 1 AND workspace_id IS NULL) OR (@systemOnly = 0 AND (@workspaceId IS NULL OR workspace_id = @workspaceId))) " +
            "AND (@liveOnly = 0 OR state IN (" + LiveStates + ")) " +
            "ORDER BY workspace_name, name",
            new { systemOnly = query.SystemOnly ? 1 : 0, workspaceId = query.WorkspaceId, liveOnly = query.LiveOnly ? 1 : 0 },
            cancellationToken: ct));
        return rows.Select(ToTerminal).ToList();
    }

    public Task<bool> UpdateAsync(long id, TerminalUpdate update, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(update);
        var runColumns = string.Join(", ", RunColumns.Select(column =>
            $"{column} = CASE WHEN @startRun = 1 THEN @{Parameter(column)} ELSE COALESCE(@{Parameter(column)}, {column}) END"));
        return writes.ExecuteAsync<bool>(async (connection, tx, k) =>
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE terminals SET " +
                "state = COALESCE(@state, state), " +
                "current_run_id = CASE WHEN @clearCurrent = 1 THEN NULL ELSE COALESCE(@currentRunId, current_run_id) END, " +
                "last_run_id = COALESCE(@lastRunId, last_run_id), operation = COALESCE(@operation, operation), " +
                "command_line = COALESCE(@commandLine, command_line), folder = COALESCE(@folder, folder), " +
                "urls_json = CASE WHEN @startRun = 1 THEN COALESCE(@urls, '[]') ELSE COALESCE(@urls, urls_json) END, " +
                "ports_json = CASE WHEN @startRun = 1 THEN COALESCE(@ports, '[]') ELSE COALESCE(@ports, ports_json) END, " +
                runColumns + ", " +
                "started_utc = COALESCE(@startedUtc, started_utc), last_output_utc = COALESCE(@lastOutputUtc, last_output_utc), " +
                "approved_json = CASE WHEN @clearApproval = 1 THEN NULL ELSE COALESCE(@approval, approved_json) END, " +
                "launch_json = COALESCE(@launch, launch_json), updated_utc = @now " +
                "WHERE id = @id",
                new
                {
                    id,
                    startRun = update.StartRun ? 1 : 0,
                    clearCurrent = update.ClearCurrentRun ? 1 : 0,
                    clearApproval = update.ClearApproval ? 1 : 0,
                    state = update.State is { } state ? EnumText.ToDb(state) : null,
                    currentRunId = update.CurrentRunId,
                    lastRunId = update.LastRunId,
                    operation = update.Operation,
                    commandLine = update.CommandLine,
                    folder = update.Folder,
                    urls = update.Urls is null ? null : JsonSerializer.Serialize(update.Urls, Json),
                    ports = update.Ports is null ? null : JsonSerializer.Serialize(update.Ports, Json),
                    pid = update.Pid,
                    pidStartUtc = Timestamp.FormatNullable(update.PidStartUtc),
                    program = update.Program,
                    exitCode = update.ExitCode,
                    stateNote = update.StateNote,
                    stoppedBy = update.StoppedBy,
                    readyHow = update.ReadyHow,
                    readyUtc = Timestamp.FormatNullable(update.ReadyUtc),
                    endedUtc = Timestamp.FormatNullable(update.EndedUtc),
                    startedUtc = Timestamp.FormatNullable(update.StartedUtc),
                    lastOutputUtc = Timestamp.FormatNullable(update.LastOutputUtc),
                    approval = update.Approval is null ? null : JsonSerializer.Serialize(update.Approval, Json),
                    launch = update.Launch is null ? null : JsonSerializer.Serialize(update.Launch, Json),
                    now = Timestamp.Format(DateTimeOffset.UtcNow),
                },
                tx,
                cancellationToken: k)) > 0,
            ct);
    }

    public Task<bool> DeleteAsync(long id, CancellationToken ct) =>
        writes.ExecuteAsync<bool>(async (connection, tx, k) =>
            await connection.ExecuteAsync(new CommandDefinition("DELETE FROM terminals WHERE id = @id", new { id }, tx, cancellationToken: k)) > 0,
            ct);

    public Task<int> ClearApprovalsAsync(long workspaceId, CancellationToken ct) =>
        writes.ExecuteAsync<int>(async (connection, tx, k) =>
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE terminals SET approved_json = NULL WHERE approved_json IS NOT NULL AND " +
                "((@workspaceId = 0 AND workspace_id IS NULL) OR workspace_id = @workspaceId)",
                new { workspaceId },
                tx,
                cancellationToken: k)),
            ct);

    public Task<int> DeleteUnusedBeforeAsync(DateTimeOffset cutoff, CancellationToken ct) =>
        writes.ExecuteAsync<int>(async (connection, tx, k) =>
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM terminals WHERE state NOT IN (" + LiveStates + ") AND updated_utc < @cut",
                new { cut = Timestamp.Format(cutoff) },
                tx,
                cancellationToken: k)),
            ct);

    private static string Parameter(string column)
    {
        var parts = column.Split('_');
        return parts[0] + string.Concat(parts.Skip(1).Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
    }

    private static Terminal ToTerminal(Row row) =>
        new(
            row.Id,
            row.WorkspaceId,
            row.WorkspaceName,
            row.Name,
            row.Folder,
            EnumText.FromDb<TerminalState>(row.State),
            row.CurrentRunId,
            row.LastRunId,
            row.Operation,
            row.CommandLine,
            Read<List<string>>(row.UrlsJson) ?? [],
            Read<List<int>>(row.PortsJson) ?? [],
            row.Pid is { } pid ? (int)pid : null,
            Timestamp.ParseNullable(row.PidStartUtc),
            row.Program,
            row.ExitCode is { } code ? (int)code : null,
            row.StateNote,
            row.StoppedBy,
            row.ReadyHow,
            Timestamp.ParseNullable(row.StartedUtc),
            Timestamp.ParseNullable(row.ReadyUtc),
            Timestamp.ParseNullable(row.EndedUtc),
            Timestamp.ParseNullable(row.LastOutputUtc),
            row.ApprovedJson is null ? null : Read<TerminalApproval>(row.ApprovedJson),
            row.LaunchJson is null ? null : Read<TerminalLaunchSpec>(row.LaunchJson),
            Timestamp.Parse(row.CreatedUtc),
            Timestamp.Parse(row.UpdatedUtc));

    private static T? Read<T>(string json)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed class Row
    {
        public long Id { get; set; }

        public long? WorkspaceId { get; set; }

        public string WorkspaceName { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string Folder { get; set; } = string.Empty;

        public string State { get; set; } = "idle";

        public long? CurrentRunId { get; set; }

        public long? LastRunId { get; set; }

        public string? Operation { get; set; }

        public string? CommandLine { get; set; }

        public string UrlsJson { get; set; } = "[]";

        public string PortsJson { get; set; } = "[]";

        public long? Pid { get; set; }

        public string? PidStartUtc { get; set; }

        public string? Program { get; set; }

        public long? ExitCode { get; set; }

        public string? StateNote { get; set; }

        public string? StoppedBy { get; set; }

        public string? ReadyHow { get; set; }

        public string? StartedUtc { get; set; }

        public string? ReadyUtc { get; set; }

        public string? EndedUtc { get; set; }

        public string? LastOutputUtc { get; set; }

        public string? ApprovedJson { get; set; }

        public string? LaunchJson { get; set; }

        public string CreatedUtc { get; set; } = string.Empty;

        public string UpdatedUtc { get; set; } = string.Empty;
    }
}
