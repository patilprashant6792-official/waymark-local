using System.Text.RegularExpressions;
using Waymark.Core.Workspaces;

namespace Waymark.Core.Security;

internal sealed partial class PathGuard(IWorkspaceStore workspaces, FileAccessPolicy policy, IAccessRuleCache accessRules) : IPathGuard
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public async Task<Workspace> GetWorkspaceAsync(string? workspaceName, CancellationToken ct)
    {
        var name = workspaceName?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException("Give a workspace name.", nameof(workspaceName));
        }

        if (name == "*")
        {
            throw new ArgumentException("This tool works on one workspace at a time; '*' is not accepted here.", nameof(workspaceName));
        }

        var workspace = await workspaces.GetByNameAsync(name, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Workspace '{name}' not found. Use code_skeleton with workspace '*' to list the available workspaces.");
        if (!workspace.Enabled)
        {
            throw new InvalidOperationException($"Workspace '{workspace.Name}' is disabled in the admin UI.");
        }

        // Loads this workspace's access rules, so the synchronous Resolve that follows reads them from memory.
        await accessRules.GetAsync(workspace.Id, ct).ConfigureAwait(false);
        return workspace;
    }

    public GuardedPath Resolve(Workspace workspace, string? relativePath)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var input = relativePath?.Trim() ?? string.Empty;
        if (input.Contains('\0'))
        {
            throw new FileAccessDeniedException(input, "The path contains invalid characters.");
        }

        if (Path.IsPathRooted(input))
        {
            throw new FileAccessDeniedException(input, "Use a path relative to the workspace folder.");
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace.Path));
        string absolute;
        try
        {
            absolute = Path.TrimEndingDirectorySeparator(Path.GetFullPath(input, root));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new FileAccessDeniedException(input, "The path is not valid.");
        }

        if (!IsInside(root, absolute))
        {
            throw new FileAccessDeniedException(input, "The path leaves the workspace folder.");
        }

        var relative = ToRelative(root, absolute);
        RejectAlternateNames(relative);
        var rules = accessRules.Get(workspace.Id);
        policy.EnsureAllowed(relative, rules);
        EnsureNoLinkEscape(root, relative, rules);
        return new GuardedPath(workspace, relative, absolute);
    }

    public async Task<GuardedPath> ResolveAsync(string? workspaceName, string? relativePath, CancellationToken ct) =>
        Resolve(await GetWorkspaceAsync(workspaceName, ct).ConfigureAwait(false), relativePath);

    private static bool IsInside(string root, string path) =>
        path.Equals(root, PathComparison) || path.StartsWith(root + Path.DirectorySeparatorChar, PathComparison);

    private static string ToRelative(string root, string absolute) =>
        absolute.Length == root.Length ? string.Empty : absolute[(root.Length + 1)..].Replace('\\', '/');

    // 'name.ext:stream' (NTFS alternate data stream) and 'LONGNA~1.EXT' (8.3 short name) reach a file under a different name
    // than the one the policy judged, so both are refused outright.
    private static void RejectAlternateNames(string relative)
    {
        if (relative.Contains(':'))
        {
            throw new FileAccessDeniedException(relative, "A ':' is not allowed in a path.");
        }

        if (ShortNameRegex().IsMatch(relative))
        {
            throw new FileAccessDeniedException(relative, "Short (8.3) names are not accepted; use the full file or folder name.");
        }
    }

    // Every existing folder or file on the way is checked: a link may stay inside the workspace, never leave it,
    // and where it ends up is judged by the same policy.
    private void EnsureNoLinkEscape(string root, string relative, WorkspaceAccessRules rules)
    {
        var current = root;
        foreach (var segment in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (!info.Exists)
            {
                return;
            }

            if (info.LinkTarget is null)
            {
                continue;
            }

            FileSystemInfo? target;
            try
            {
                target = info.ResolveLinkTarget(returnFinalTarget: true);
            }
            catch (IOException)
            {
                throw new FileAccessDeniedException(relative, "A link on the path cannot be followed.");
            }

            var targetPath = target is null ? null : Path.TrimEndingDirectorySeparator(target.FullName);
            if (targetPath is null || !IsInside(root, targetPath))
            {
                throw new FileAccessDeniedException(relative, "A link on the path points outside the workspace folder.");
            }

            policy.EnsureAllowed(ToRelative(root, targetPath), rules);
        }
    }

    [GeneratedRegex(@"~\d")]
    private static partial Regex ShortNameRegex();
}
