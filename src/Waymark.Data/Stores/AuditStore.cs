using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Waymark.Core.Audit;
using Waymark.Core.LiveFeed;
using Waymark.Data.Sqlite;

namespace Waymark.Data.Stores;

public sealed class AuditStore(ISqliteConnectionFactory connections, IWriteQueue writes, ILogger<AuditStore> logger) : IAuditStore
{
    private const string InsertSql =
        "INSERT INTO audit_entries (ts_utc, category, actor, action, target, args_redacted, duration_ms, result_bytes, outcome, error, result_tokens) " +
        "VALUES (@ts, @category, @actor, @action, @target, @args, @durationMs, @resultBytes, @outcome, @error, @resultTokens)";

    public async Task AppendAsync(AuditEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await writes.RunAsync(async (connection, tx, k) => await InsertAsync(connection, tx, entry, k), ct);
        ShowLive(entry);
    }

    public async Task AppendBatchAsync(IReadOnlyList<AuditEntry> entries, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
        {
            return;
        }

        await writes.RunAsync(async (connection, tx, k) =>
        {
            foreach (var entry in entries)
            {
                await InsertAsync(connection, tx, entry, k);
            }
        }, ct);

        foreach (var entry in entries)
        {
            ShowLive(entry);
        }
    }

    /// <summary>
    /// The audit trail is stored. Sign-in and approval entries are also shown on the Live page (in memory only). Tool calls are shown by the
    /// tool pipeline, which knows the arguments; configuration changes and command decisions have their own places.
    /// </summary>
    private void ShowLive(AuditEntry entry)
    {
        if (entry.Category is not (AuditCategory.Auth or AuditCategory.Approval) || entry.Action.StartsWith("command.", StringComparison.Ordinal))
        {
            return;
        }

        var outcome = entry.Outcome.ToString().ToLowerInvariant();
        if (entry.Error is not null)
        {
            outcome += ": " + entry.Error;
        }

        var level = entry.Outcome switch
        {
            AuditOutcome.Error => LogLevel.Error,
            AuditOutcome.Denied => LogLevel.Warning,
            _ => LogLevel.Information
        };
        logger.LogAuth(level, entry.Action, entry.Actor ?? "system", entry.Target ?? "-", outcome);
    }

    public async Task<IReadOnlyList<StoredAuditEntry>> QueryAsync(AuditQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Limit is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(query), "The limit must be between 1 and 500.");
        }

        await using var connection = await connections.OpenAsync(ct);
        var rows = await connection.QueryAsync<AuditRow>(new CommandDefinition(
            "SELECT id, ts_utc, category, actor, action, target, args_redacted, duration_ms, result_bytes, outcome, error, result_tokens FROM audit_entries " +
            FilterSql + "AND (@beforeId IS NULL OR id < @beforeId) AND (@afterId IS NULL OR id > @afterId) ORDER BY id DESC LIMIT @limit",
            FilterParameters(query),
            cancellationToken: ct));

        return rows.Select(ToStored).ToList();
    }

    public async Task<int> CountAsync(AuditQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using var connection = await connections.OpenAsync(ct);
        return await connection.QuerySingleAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM audit_entries " + FilterSql,
            FilterParameters(query),
            cancellationToken: ct));
    }

    public async Task<IReadOnlyList<string>> DistinctActionsAsync(AuditCategory category, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var values = await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT DISTINCT action FROM audit_entries WHERE category = @category ORDER BY action",
            new { category = EnumText.ToDb(category) },
            cancellationToken: ct));
        return values.ToList();
    }

    public async Task<IReadOnlyList<string>> DistinctTargetsAsync(AuditCategory category, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var values = await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT DISTINCT target FROM audit_entries WHERE category = @category AND target IS NOT NULL AND target <> '' ORDER BY target",
            new { category = EnumText.ToDb(category) },
            cancellationToken: ct));
        return values.ToList();
    }

    // The counted tokens of a call when it has them; for older rows the estimate from the size, rounded up per call like TokenEstimate.FromBytes: (bytes + 3) / 4.
    private const string UsageColumns = "COUNT(*) AS Calls, IFNULL(SUM(COALESCE(result_tokens, (IFNULL(result_bytes, 0) + 3) / 4)), 0) AS Tokens";
    private const string UsageWhere = "WHERE category = @category AND (@since IS NULL OR ts_utc >= @since) ";
    private const int MaxUsageTools = 50;

    public async Task<ToolUsage> UsageAsync(DateTimeOffset? sinceUtc, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var row = await connection.QuerySingleAsync<UsageRow>(new CommandDefinition(
            "SELECT " + UsageColumns + " FROM audit_entries " + UsageWhere,
            UsageParameters(sinceUtc, 1),
            cancellationToken: ct));
        return ToUsage(row);
    }

    public async Task<IReadOnlyList<ToolUsageByTool>> UsageByToolAsync(DateTimeOffset? sinceUtc, int limit, CancellationToken ct)
    {
        if (limit is < 1 or > MaxUsageTools)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), $"The limit must be between 1 and {MaxUsageTools}.");
        }

        await using var connection = await connections.OpenAsync(ct);
        var rows = await connection.QueryAsync<UsageByToolRow>(new CommandDefinition(
            "SELECT action AS Tool, " + UsageColumns + " FROM audit_entries " + UsageWhere + "GROUP BY action ORDER BY Tokens DESC, action LIMIT @limit",
            UsageParameters(sinceUtc, limit),
            cancellationToken: ct));
        return rows.Select(row => new ToolUsageByTool(row.Tool, ToUsage(row))).ToList();
    }

    private static object UsageParameters(DateTimeOffset? sinceUtc, int limit) =>
        new
        {
            category = EnumText.ToDb(AuditCategory.Tool),
            since = sinceUtc is null ? null : Timestamp.Format(sinceUtc.Value),
            limit
        };

    private static ToolUsage ToUsage(UsageRow row) => new(checked((int)row.Calls), row.Tokens);

    private const string FilterSql =
        "WHERE (@category IS NULL OR category = @category) AND (@outcome IS NULL OR outcome = @outcome) " +
        "AND (@action IS NULL OR action = @action) AND (@target IS NULL OR target = @target) " +
        "AND (@since IS NULL OR ts_utc >= @since) " +
        "AND (@text IS NULL OR action LIKE @text ESCAPE '\\' OR IFNULL(target, '') LIKE @text ESCAPE '\\' " +
        "OR IFNULL(actor, '') LIKE @text ESCAPE '\\' OR IFNULL(error, '') LIKE @text ESCAPE '\\') ";

    private static object FilterParameters(AuditQuery query) =>
        new
        {
            category = query.Category is null ? null : EnumText.ToDb(query.Category.Value),
            outcome = query.Outcome is null ? null : EnumText.ToDb(query.Outcome.Value),
            action = string.IsNullOrWhiteSpace(query.Action) ? null : query.Action.Trim(),
            target = string.IsNullOrWhiteSpace(query.Target) ? null : query.Target.Trim(),
            since = query.SinceUtc is null ? null : Timestamp.Format(query.SinceUtc.Value),
            text = string.IsNullOrWhiteSpace(query.Text) ? null : "%" + EscapeLike(query.Text.Trim()) + "%",
            beforeId = query.BeforeId,
            afterId = query.AfterId,
            limit = query.Limit
        };

    private static string EscapeLike(string text) =>
        text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);

    public Task<int> PruneAsync(DateTimeOffset olderThan, CancellationToken ct) =>
        writes.ExecuteAsync<int>(async (connection, tx, k) =>
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM audit_entries WHERE ts_utc < @olderThan",
                new { olderThan = Timestamp.Format(olderThan) },
                tx,
                cancellationToken: k)),
            ct);

    private static Task InsertAsync(SqliteConnection connection, SqliteTransaction tx, AuditEntry entry, CancellationToken ct) =>
        connection.ExecuteAsync(new CommandDefinition(
            InsertSql,
            new
            {
                ts = Timestamp.Format(entry.TimestampUtc),
                category = EnumText.ToDb(entry.Category),
                actor = entry.Actor,
                action = entry.Action,
                target = entry.Target,
                args = entry.ArgsRedacted,
                durationMs = entry.DurationMs,
                resultBytes = entry.ResultBytes,
                resultTokens = entry.ResultTokens,
                outcome = EnumText.ToDb(entry.Outcome),
                error = entry.Error
            },
            tx,
            cancellationToken: ct));

    private static StoredAuditEntry ToStored(AuditRow row) =>
        new(
            row.Id,
            new AuditEntry(
                Timestamp.Parse(row.TsUtc),
                EnumText.FromDb<AuditCategory>(row.Category),
                row.Actor,
                row.Action,
                row.Target,
                row.ArgsRedacted,
                row.DurationMs is null ? null : checked((int)row.DurationMs.Value),
                row.ResultBytes is null ? null : checked((int)row.ResultBytes.Value),
                EnumText.FromDb<AuditOutcome>(row.Outcome),
                row.Error,
                row.ResultTokens is null ? null : checked((int)row.ResultTokens.Value)));

    private sealed class AuditRow
    {
        public long Id { get; set; }

        public string TsUtc { get; set; } = string.Empty;

        public string Category { get; set; } = string.Empty;

        public string? Actor { get; set; }

        public string Action { get; set; } = string.Empty;

        public string? Target { get; set; }

        public string? ArgsRedacted { get; set; }

        public long? DurationMs { get; set; }

        public long? ResultBytes { get; set; }

        public string Outcome { get; set; } = string.Empty;

        public string? Error { get; set; }

        public long? ResultTokens { get; set; }
    }

    private class UsageRow
    {
        public long Calls { get; set; }

        public long Tokens { get; set; }
    }

    private sealed class UsageByToolRow : UsageRow
    {
        public string Tool { get; set; } = string.Empty;
    }
}
