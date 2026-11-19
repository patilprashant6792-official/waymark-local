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
        "Lists the files directly inside one folder of a workspace (not its subfolders), sorted by name and paged, each with its path, size and line count. " +
        "Each file is marked small (read it with code_read) or large (use code_outline + code_method). Optionally only names that contain some text. " +
        "Files tools may not open (build output, .git, secrets, keys, the workspace's access rules) are not listed. A folder that does not exist is an error. Reads only.\n" +
        "WHEN TO USE: code_skeleton showed a folder as collapsed or too big to list, or you need every file of one folder, e.g. all '*Transaction*' files in Entities/Exchange. " +
        "For the folder tree use code_skeleton; to find a type or member anywhere use code_search; for text inside files use code_grep.\n" +
        "RETURNS: 'Showing N of M file(s) - page p/q', the files, and the call for the next page.")]
    public Task<string> FolderFiles(
        [Description("Workspace name as shown in Waymark (a single workspace; '*' is not accepted)")]
        string workspace,
        [Description("Folder path relative to the workspace root (e.g. 'Entities/Exchange', 'Controllers'). '.' is the root.")]
        string folderPath,
        [Description("Optional: text the file NAME must contain, any letter case, e.g. 'Company' or '.cs'. Plain text, not a glob ('*' is not a wildcard). Leave empty to list all.")]
        string? searchPattern = null,
        [Description("Optional: page number, starting at 1 (default 1)")]
        int page = 1,
        [Description("Optional: files per page (default 50, at most 200)")]
        int pageSize = 50,
        CancellationToken cancellationToken = default) =>
        RunAsync(() => skeleton.ListFolderFilesAsync(workspace, folderPath, searchPattern, page, pageSize, cancellationToken));

    [McpServerTool(Name = "code_changes_since", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description(
        "Lists every file in a workspace, in all folders, whose last-modified time on disk is after a point in time, in path order, each with its modified time (UTC), size and line count. " +
        "Files tools may not open (build output, .git, secrets, the workspace's access rules) are left out. At most 500 files are listed; the reply says when the list was cut, so use a later time to narrow it. Reads only.\n" +
        "WHEN TO USE: resuming work after a break, or checking what you, the owner or a tool changed since a moment. " +
        "It reads file times, not git: for uncommitted changes against the last commit use cmd_git status or diff; for the folder layout use code_skeleton.")]
    public Task<string> ChangesSince(
        [Description("Workspace name as shown in Waymark (a single workspace; '*' is not accepted)")]
        string workspace,
        [Description("The point in time: Unix seconds ('1705449600') or ISO 8601 ('2026-01-17T09:30:00Z', '2026-01-17T15:00:00+05:30'). A date or time without an offset is read as UTC, so add the owner's offset for a local time (util_datetime gives it).")]
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
