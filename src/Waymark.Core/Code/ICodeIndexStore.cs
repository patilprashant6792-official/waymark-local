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

    /// <summary>
    /// Stores and removes many files in one transaction. Every file is written under its own savepoint, so a file that cannot be stored is rolled back
    /// alone and reported in the result while the others commit. Paths are compared ignoring case.
    /// </summary>
    Task<CodeBatchResult> ApplyBatchAsync(long workspaceId, IReadOnlyList<CodeFileIndex> replace, IReadOnlyList<string> remove, DateTimeOffset indexedUtc, CancellationToken ct);

    /// <summary>The stamps of the given paths that are indexed, read on one connection. A path that is not indexed is missing from the result.</summary>
    Task<IReadOnlyDictionary<string, CodeFileStamp>> GetStampsAsync(long workspaceId, IReadOnlyCollection<string> relativePaths, CancellationToken ct);

    /// <summary>The stamps of every indexed file inside one folder (a workspace-relative path with '/'), for a reconcile of that folder alone.</summary>
    Task<IReadOnlyList<CodeFileStamp>> ListStampsUnderAsync(long workspaceId, string folder, CancellationToken ct);
}
