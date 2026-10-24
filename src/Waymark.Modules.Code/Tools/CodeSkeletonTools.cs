using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Waymark.Core.Security;
using Waymark.Modules.Code.Skeleton;

namespace Waymark.Modules.Code.Tools;

[McpServerToolType]
public sealed class CodeSkeletonTools(ISkeletonService skeleton, ILogger<CodeSkeletonTools> logger)
{
    [McpServerTool(Name = "code_skeleton", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description(
        "Retrieves the structure of a workspace, or of one folder in it: ASCII folder tree with file sizes and line counts, solution files, " +
        "and a NuGet summary per project (package versions, SDK, target framework). " +
        "WILDCARD: pass workspace='*' to list all registered workspaces. " +
        "OVERVIEW (no depth): folders open level by level until a size budget is used; every folder left closed is marked ▸ with a one-line " +
        "summary (folders, files, size, large files, main extensions, project file), so even a very large workspace fits. " +
        "Open a ▸ folder by calling again with its path; pass depth to show an exact number of levels (a large depth shows the whole tree). " +
        "A folder with many entries is paged with page; folders with many files are collapsed, list them with code_folder_files. " +
        "To see what changed since a point in time use code_changes_since. " +
        "Use this BEFORE any change to understand the existing structure.")]
    public Task<string> Skeleton(
        [Description("Workspace name as shown in Waymark. Use '*' to list all workspaces.")]
        string workspace,
        [Description("Optional: folder to show, relative to the workspace root (e.g. 'src/Orders'). Leave empty for the root.")]
        string? path = null,
        [Description("Optional: levels below the folder to show (1 = its direct entries). Leave empty for the overview, which opens folders until its size budget is used.")]
        int? depth = null,
        [Description("Optional: page of the folder's own entries when it has more than fit at once, starting at 1 (default 1)")]
        int page = 1,
        CancellationToken cancellationToken = default) =>
        RunAsync(() => skeleton.BuildSkeletonAsync(workspace, new SkeletonRequest(path, depth, page), cancellationToken));

    [McpServerTool(Name = "code_folder_files", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description(
        "Search and paginate the files directly inside one folder of a workspace. " +
        "Use this when code_skeleton shows 'N files collapsed'. " +
        "Supports filtering by file name (case-insensitive). Returns sorted, paginated results with size and line count. " +
        "Example: find all 'Transaction' files in Entities/Exchange.")]
    public Task<string> FolderFiles(
        [Description("Workspace name as shown in Waymark (a single workspace; '*' is not accepted)")]
        string workspace,
        [Description("Folder path relative to the workspace root (e.g. 'Entities/Exchange', 'Controllers'). '.' is the root.")]
        string folderPath,
        [Description("Optional: part of the file name to filter by (case-insensitive, e.g. 'Company'). Leave empty to list all.")]
        string? searchPattern = null,
        [Description("Optional: page number, starting at 1 (default 1)")]
        int page = 1,
        [Description("Optional: files per page (default 50, at most 200)")]
        int pageSize = 50,
        CancellationToken cancellationToken = default) =>
        RunAsync(() => skeleton.ListFolderFilesAsync(workspace, folderPath, searchPattern, page, pageSize, cancellationToken));

    [McpServerTool(Name = "code_changes_since", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description(
        "Lists the files of a workspace that were modified after a point in time, with modified time, size and line count. " +
        "Use it when resuming work or to review recent edits. " +
        "The timestamp is a Unix timestamp in seconds or an ISO 8601 date; a date without an offset is taken as UTC.")]
    public Task<string> ChangesSince(
        [Description("Workspace name as shown in Waymark (a single workspace; '*' is not accepted)")]
        string workspace,
        [Description("Unix timestamp (e.g. '1705449600') or ISO 8601 date (e.g. '2026-01-17T00:00:00Z')")]
        string sinceTimestamp,
        CancellationToken cancellationToken = default) =>
        RunAsync(() => skeleton.ChangesSinceAsync(workspace, sinceTimestamp, cancellationToken));

    private async Task<string> RunAsync(Func<Task<string>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FileAccessDeniedException or ArgumentException or KeyNotFoundException or InvalidOperationException or DirectoryNotFoundException)
        {
            throw new McpException(ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "A workspace overview tool could not read the file system.");
            throw new McpException("The folder could not be read. See the Waymark log for details.");
        }
    }
}
