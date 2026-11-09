using Waymark.Core.Audit;
using Waymark.Core.Vault;

namespace Waymark.Tests.Support;

/// <summary>An audit store that keeps appended entries in a list. Only the append calls work; the rest are not needed by these tests.</summary>
internal sealed class FakeAuditStore : IAuditStore
{
    public List<AuditEntry> Entries { get; } = [];

    /// <summary>When set, every append throws it.</summary>
    public Exception? FailWith { get; set; }

    public Task AppendAsync(AuditEntry entry, CancellationToken ct)
    {
        if (FailWith is not null)
        {
            throw FailWith;
        }

        lock (Entries)
        {
            Entries.Add(entry);
        }

        return Task.CompletedTask;
    }

    public Task AppendBatchAsync(IReadOnlyList<AuditEntry> entries, CancellationToken ct) => throw new NotSupportedException();

    public Task<long> AppendToolCallAsync(AuditEntry entry, ToolPayload? payload, CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyList<StoredAuditEntry>> QueryAsync(AuditQuery query, CancellationToken ct) => throw new NotSupportedException();

    public Task<StoredAuditEntry?> GetAsync(long id, CancellationToken ct) => throw new NotSupportedException();

    public Task<StoredToolPayload?> GetPayloadAsync(long id, CancellationToken ct) => throw new NotSupportedException();

    public Task<string?> GetPayloadOutputAsync(long id, CancellationToken ct) => throw new NotSupportedException();

    public Task<int> PrunePayloadsAsync(DateTimeOffset olderThan, CancellationToken ct) => throw new NotSupportedException();

    public Task<int> ClearPayloadsAsync(CancellationToken ct) => throw new NotSupportedException();

    public Task<ToolPayloadStats> PayloadStatsAsync(CancellationToken ct) => throw new NotSupportedException();

    public Task<int> CountAsync(AuditQuery query, CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyList<string>> DistinctActionsAsync(AuditCategory category, CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyList<string>> DistinctTargetsAsync(AuditCategory category, CancellationToken ct) => throw new NotSupportedException();

    public Task<ToolUsage> UsageAsync(DateTimeOffset? sinceUtc, CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyList<ToolUsageByTool>> UsageByToolAsync(DateTimeOffset? sinceUtc, int limit, CancellationToken ct) => throw new NotSupportedException();

    public Task<int> PruneAsync(DateTimeOffset olderThan, CancellationToken ct) => throw new NotSupportedException();
}

/// <summary>A vault store whose every read fails, to prove the callers keep working without the vault.</summary>
internal sealed class BrokenVaultStore : IVaultStore
{
    public Task<IReadOnlyList<VaultSecret>> ListAsync(CancellationToken ct) => throw new InvalidOperationException("vault unavailable");

    public Task<string?> GetProtectedAsync(string name, CancellationToken ct) => throw new InvalidOperationException("vault unavailable");

    public Task<IReadOnlyList<KeyValuePair<string, string>>> ListProtectedAsync(CancellationToken ct) => throw new InvalidOperationException("vault unavailable");

    public Task<bool> UpsertAsync(string name, string protectedValue, string hint, DateTimeOffset now, CancellationToken ct) => throw new InvalidOperationException("vault unavailable");

    public Task<bool> MarkUsedAsync(string name, DateTimeOffset now, CancellationToken ct) => throw new InvalidOperationException("vault unavailable");

    public Task<bool> DeleteAsync(string name, CancellationToken ct) => throw new InvalidOperationException("vault unavailable");
}
