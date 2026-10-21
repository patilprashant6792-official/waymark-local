namespace Waymark.Modules.Code.Skeleton;

/// <summary>Markdown overviews of a workspace: tree, changed files and folder listings. Every path goes through the path guard.</summary>
public interface ISkeletonService
{
    /// <summary>The registered workspaces with their folders and index state.</summary>
    Task<string> ListWorkspacesAsync(CancellationToken ct);

    /// <summary>
    /// Folder tree, solution files and NuGet summary of one workspace. '*' lists the workspaces and an unknown name
    /// answers with the workspace list instead of failing.
    /// </summary>
    /// <exception cref="InvalidOperationException">The workspace is disabled.</exception>
    /// <exception cref="DirectoryNotFoundException">The workspace folder no longer exists.</exception>
    Task<string> BuildSkeletonAsync(string? workspace, CancellationToken ct);

    /// <summary>Files modified after the timestamp (Unix seconds or ISO 8601, UTC assumed when no offset is given).</summary>
    /// <exception cref="ArgumentException">The workspace name or the timestamp is not valid.</exception>
    /// <exception cref="KeyNotFoundException">No workspace has that name.</exception>
    Task<string> ChangesSinceAsync(string? workspace, string? sinceTimestamp, CancellationToken ct);

    /// <summary>One page of the files directly inside a folder. '.' or an empty folder means the workspace root.</summary>
    /// <exception cref="DirectoryNotFoundException">The folder does not exist.</exception>
    /// <exception cref="Waymark.Core.Security.FileAccessDeniedException">The folder is refused by the access policy.</exception>
    Task<string> ListFolderFilesAsync(string? workspace, string? folderPath, string? searchPattern, int page, int pageSize, CancellationToken ct);
}
