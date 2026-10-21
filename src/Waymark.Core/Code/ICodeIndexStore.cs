namespace Waymark.Core.Code;

public interface ICodeIndexStore
{
    /// <summary>
    /// Stores one file, replacing any earlier version of the same path (compared ignoring case) in one transaction,
    /// so readers never see a half-replaced file. Throws when the workspace does not exist.
    /// </summary>
    Task ReplaceFileAsync(long workspaceId, CodeFileIndex file, DateTimeOffset indexedUtc, CancellationToken ct);

    /// <summary>False when the path was not indexed.</summary>
    Task<bool> RemoveFileAsync(long workspaceId, string relativePath, CancellationToken ct);

    /// <summary>Forgets every file of the workspace (used before a full re-index). Returns the number of files removed.</summary>
    Task<int> ClearWorkspaceAsync(long workspaceId, CancellationToken ct);

    /// <summary>The stamp of every indexed file, for the start-up reconcile against the disk.</summary>
    Task<IReadOnlyList<CodeFileStamp>> ListStampsAsync(long workspaceId, CancellationToken ct);

    /// <summary>Null when the path is not indexed.</summary>
    Task<CodeFileIndex?> GetFileAsync(long workspaceId, string relativePath, CancellationToken ct);

    Task<CodeIndexStats> GetStatsAsync(long workspaceId, CancellationToken ct);
}
