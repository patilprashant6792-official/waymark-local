namespace Waymark.Core.Audit;

public interface IAuditStore
{
    Task AppendAsync(AuditEntry entry, CancellationToken ct);

    Task AppendBatchAsync(IReadOnlyList<AuditEntry> entries, CancellationToken ct);

    Task<IReadOnlyList<StoredAuditEntry>> QueryAsync(AuditQuery query, CancellationToken ct);

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
