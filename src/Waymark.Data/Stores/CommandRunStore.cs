using System.Text.Json;
using Dapper;
using Waymark.Core.Commands;
using Waymark.Data.Sqlite;

namespace Waymark.Data.Stores;

public sealed class CommandRunStore(ISqliteConnectionFactory connections, IWriteQueue writes) : ICommandRunStore
{
    private const int MaxLimit = 500;
    private const string Columns =
        "id, workspace_id, workspace_name, tier, operation, shell, command_line, original_command, working_dir, purpose, status, exit_code, " +
        "requested_utc, decided_utc, started_utc, ended_utc, actor, decided_by, ask_reason, note, risks, output_bytes, output_path";
    private const string ActiveStatuses = "'waitingapproval', 'queued', 'running'";

    public Task<CommandRun> CreateAsync(NewCommandRun run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        return writes.ExecuteAsync<CommandRun>(async (connection, tx, k) =>
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO command_runs (workspace_id, workspace_name, tier, operation, shell, command_line, working_dir, purpose, status, requested_utc, actor, ask_reason, risks) " +
                "VALUES (@workspaceId, @workspaceName, @tier, @operation, @shell, @commandLine, @workingDir, @purpose, @status, @requested, @actor, @askReason, @risks)",
                new
                {
                    workspaceId = run.WorkspaceId,
                    workspaceName = run.WorkspaceName,
                    tier = EnumText.ToDb(run.Tier),
                    operation = run.Operation,
                    shell = run.Shell,
                    commandLine = run.CommandLine,
                    workingDir = run.WorkingDirectory,
                    purpose = run.Purpose,
                    status = EnumText.ToDb(run.Status),
                    requested = Timestamp.Format(run.RequestedUtc),
                    actor = run.Actor,
                    askReason = run.AskReason,
                    risks = JsonSerializer.Serialize(run.Risks),
                },
                tx,
                cancellationToken: k));
            var row = await connection.QuerySingleAsync<RunRow>(new CommandDefinition(
                "SELECT " + Columns + " FROM command_runs WHERE id = last_insert_rowid()",
                transaction: tx,
                cancellationToken: k));
            return ToRun(row);
        }, ct);
    }

    public async Task<CommandRun?> GetAsync(long id, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<RunRow>(new CommandDefinition(
            "SELECT " + Columns + " FROM command_runs WHERE id = @id",
            new { id },
            cancellationToken: ct));
        return row is null ? null : ToRun(row);
    }

    public async Task<IReadOnlyList<CommandRun>> ListAsync(CommandRunQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using var connection = await connections.OpenAsync(ct);
        var rows = await connection.QueryAsync<RunRow>(new CommandDefinition(
            "SELECT " + Columns + " FROM command_runs " +
            "WHERE ((@systemOnly = 1 AND workspace_id IS NULL) OR (@systemOnly = 0 AND (@workspaceId IS NULL OR workspace_id = @workspaceId))) " +
            "AND (@status IS NULL OR status = @status) AND (@beforeId IS NULL OR id < @beforeId) " +
            "ORDER BY id DESC LIMIT @limit",
            new
            {
                systemOnly = query.SystemOnly ? 1 : 0,
                workspaceId = query.WorkspaceId,
                status = query.Status is { } status ? EnumText.ToDb(status) : null,
                beforeId = query.BeforeId,
                limit = Math.Clamp(query.Limit, 1, MaxLimit),
            },
            cancellationToken: ct));
        return rows.Select(ToRun).ToList();
    }

    public async Task<IReadOnlyList<CommandRun>> ListActiveAsync(CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var rows = await connection.QueryAsync<RunRow>(new CommandDefinition(
            "SELECT " + Columns + " FROM command_runs WHERE status IN (" + ActiveStatuses + ") ORDER BY id",
            cancellationToken: ct));
        return rows.Select(ToRun).ToList();
    }

    public Task<bool> UpdateAsync(long id, CommandRunUpdate update, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(update);
        return writes.ExecuteAsync<bool>(async (connection, tx, k) =>
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE command_runs SET " +
                "status = COALESCE(@status, status), exit_code = COALESCE(@exitCode, exit_code), " +
                "decided_utc = COALESCE(@decided, decided_utc), decided_by = COALESCE(@decidedBy, decided_by), " +
                "started_utc = COALESCE(@started, started_utc), ended_utc = COALESCE(@ended, ended_utc), " +
                "command_line = COALESCE(@commandLine, command_line), original_command = COALESCE(@original, original_command), " +
                "note = COALESCE(@note, note), output_bytes = COALESCE(@outputBytes, output_bytes), output_path = COALESCE(@outputPath, output_path) " +
                "WHERE id = @id",
                new
                {
                    id,
                    status = update.Status is { } status ? EnumText.ToDb(status) : null,
                    exitCode = update.ExitCode,
                    decided = Timestamp.FormatNullable(update.DecidedUtc),
                    decidedBy = update.DecidedBy,
                    started = Timestamp.FormatNullable(update.StartedUtc),
                    ended = Timestamp.FormatNullable(update.EndedUtc),
                    commandLine = update.CommandLine,
                    original = update.OriginalCommand,
                    note = update.Note,
                    outputBytes = update.OutputBytes,
                    outputPath = update.OutputPath,
                },
                tx,
                cancellationToken: k)) > 0,
            ct);
    }

    public Task<int> FailInterruptedAsync(DateTimeOffset now, CancellationToken ct) =>
        writes.ExecuteAsync<int>(async (connection, tx, k) =>
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE command_runs SET status = 'failed', ended_utc = @now, note = 'Waymark stopped before this run finished.' " +
                "WHERE status IN (" + ActiveStatuses + ")",
                new { now = Timestamp.Format(now) },
                tx,
                cancellationToken: k)),
            ct);

    public Task<IReadOnlyList<string>> DeleteEndedBeforeAsync(DateTimeOffset cutoff, CancellationToken ct) =>
        writes.ExecuteAsync<IReadOnlyList<string>>(async (connection, tx, k) =>
        {
            var cut = Timestamp.Format(cutoff);
            var paths = (await connection.QueryAsync<string?>(new CommandDefinition(
                "SELECT output_path FROM command_runs WHERE ended_utc IS NOT NULL AND ended_utc < @cut AND output_path IS NOT NULL",
                new { cut },
                tx,
                cancellationToken: k))).Where(path => !string.IsNullOrEmpty(path)).Select(path => path!).ToList();
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM command_runs WHERE ended_utc IS NOT NULL AND ended_utc < @cut",
                new { cut },
                tx,
                cancellationToken: k));
            return paths;
        }, ct);

    private static CommandRun ToRun(RunRow row) =>
        new(
            row.Id,
            row.WorkspaceId,
            row.WorkspaceName,
            EnumText.FromDb<CommandTier>(row.Tier),
            row.Operation,
            row.Shell,
            row.CommandLine,
            row.OriginalCommand,
            row.WorkingDir,
            row.Purpose,
            EnumText.FromDb<CommandRunStatus>(row.Status),
            row.ExitCode is { } code ? (int)code : null,
            Timestamp.Parse(row.RequestedUtc),
            Timestamp.ParseNullable(row.DecidedUtc),
            Timestamp.ParseNullable(row.StartedUtc),
            Timestamp.ParseNullable(row.EndedUtc),
            row.Actor,
            row.DecidedBy,
            row.AskReason,
            row.Note,
            ParseRisks(row.Risks),
            row.OutputBytes,
            row.OutputPath);

    private static IReadOnlyList<string> ParseRisks(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private sealed class RunRow
    {
        public long Id { get; set; }

        public long? WorkspaceId { get; set; }

        public string WorkspaceName { get; set; } = string.Empty;

        public string Tier { get; set; } = string.Empty;

        public string Operation { get; set; } = string.Empty;

        public string? Shell { get; set; }

        public string CommandLine { get; set; } = string.Empty;

        public string? OriginalCommand { get; set; }

        public string WorkingDir { get; set; } = string.Empty;

        public string Purpose { get; set; } = string.Empty;

        public string Status { get; set; } = string.Empty;

        public long? ExitCode { get; set; }

        public string RequestedUtc { get; set; } = string.Empty;

        public string? DecidedUtc { get; set; }

        public string? StartedUtc { get; set; }

        public string? EndedUtc { get; set; }

        public string Actor { get; set; } = string.Empty;

        public string? DecidedBy { get; set; }

        public string? AskReason { get; set; }

        public string? Note { get; set; }

        public string Risks { get; set; } = "[]";

        public long OutputBytes { get; set; }

        public string? OutputPath { get; set; }
    }
}
