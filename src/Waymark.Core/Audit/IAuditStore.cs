namespace Waymark.Core.Audit;

public interface IAuditStore
{
    Task AppendAsync(AuditEntry entry, CancellationToken ct);

    Task AppendBatchAsync(IReadOnlyList<AuditEntry> entries, CancellationToken ct);

    /// <summary>
    /// Writes the audit row of a tool call and, when there is one, its input and output, in one transaction. Returns the id of the audit row,
    /// which the details page and the live log use as the call's reference.
    /// </summary>
    Task<long> AppendToolCallAsync(AuditEntry entry, ToolPayload? payload, CancellationToken ct);

    Task<IReadOnlyList<StoredAuditEntry>> QueryAsync(AuditQuery query, CancellationToken ct);

    /// <summary>One entry by its id; null when it does not exist (or has been removed by the retention period).</summary>
    Task<StoredAuditEntry?> GetAsync(long id, CancellationToken ct);

    /// <summary>The kept input of a call and the facts about its output, without the output text; null when nothing was kept.</summary>
    Task<StoredToolPayload?> GetPayloadAsync(long id, CancellationToken ct);

    /// <summary>The kept output text of a call. Read only when the owner asks for it, because it can be long.</summary>
    Task<string?> GetPayloadOutputAsync(long id, CancellationToken ct);

    /// <summary>Deletes the kept inputs and outputs older than the moment; the audit rows stay. Returns how many were removed.</summary>
    Task<int> PrunePayloadsAsync(DateTimeOffset olderThan, CancellationToken ct);

    /// <summary>Deletes every kept input and output; the audit rows stay. Returns how many were removed.</summary>
    Task<int> ClearPayloadsAsync(CancellationToken ct);

    Task<ToolPayloadStats> PayloadStatsAsync(CancellationToken ct);

    /// <summary>How many entries match the filters of the query; BeforeId, AfterId and Limit are ignored.</summary>
    Task<int> CountAsync(AuditQuery query, CancellationToken ct);

    /// <summary>The distinct values of Action for one category (for example every tool name that has been called), sorted.</summary>
    Task<IReadOnlyList<string>> DistinctActionsAsync(AuditCategory category, CancellationToken ct);

    /// <summary>The distinct non-empty values of Target for one category (for example every workspace named in a tool call), sorted.</summary>
    Task<IReadOnlyList<string>> DistinctTargetsAsync(AuditCategory category, CancellationToken ct);

    /// <summary>The tool calls since the moment (all of them when null) and the estimated tokens their output added to the AI's context.</summary>
    Task<ToolUsage> UsageAsync(DateTimeOffset? sinceUtc, CancellationToken ct);

    /// <summary>The same figures per tool, the tool that sent the most first. The limit is 1 to 50.</summary>
    Task<IReadOnlyList<ToolUsageByTool>> UsageByToolAsync(DateTimeOffset? sinceUtc, int limit, CancellationToken ct);

    Task<int> PruneAsync(DateTimeOffset olderThan, CancellationToken ct);
}
