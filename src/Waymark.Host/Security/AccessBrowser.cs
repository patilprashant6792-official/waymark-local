using System.IO.Enumeration;
using Waymark.Core.Security;
using Waymark.Core.Workspaces;

namespace Waymark.Host.Security;

/// <summary>One file or folder as the admin UI shows it. For a folder, Allowed says whether files inside it can be opened.</summary>
public sealed record AccessEntry(string RelativePath, string Name, bool IsFolder, bool Allowed, string? Reason);

public sealed record AccessListing(IReadOnlyList<AccessEntry> Entries, bool Truncated);

/// <summary>
/// Lets the owner look through a workspace and see, for every file and folder, whether the tools may open it and why not.
/// Read-only; links are never followed. The verdict is the one the tools get: built-in policy plus the workspace's rules.
/// </summary>
public sealed class AccessBrowser(IWorkspaceStore workspaces, IAccessRuleCache cache, FileAccessPolicy policy)
{
    public const int MaxEntries = 500;
    public const int MaxResults = 200;
    public const int MaxQueryLength = 100;

    private const int MaxScanned = 20_000;

    private static readonly EnumerationOptions EntryOptions = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint,
    };

    /// <summary>Lists one folder (empty = the workspace root): folders first, then files.</summary>
    /// <exception cref="ArgumentException">The folder is not inside the workspace, or a link is on the way.</exception>
    /// <exception cref="DirectoryNotFoundException">The folder does not exist.</exception>
    /// <exception cref="KeyNotFoundException">The workspace no longer exists.</exception>
    public async Task<AccessListing> BrowseAsync(long workspaceId, string? folder, CancellationToken ct)
    {
        var (root, rules) = await OpenAsync(workspaceId, ct);
        var directory = ResolveFolder(root, NormalizeFolder(folder));
        return await Task.Run(() => List(root, directory, rules, ct), ct);
    }

    /// <summary>
    /// Finds files and folders by name. A query with * or ? is a pattern (*.sql); otherwise it is a part of the name.
    /// Folders the built-in policy always blocks are listed when they match but not searched inside.
    /// </summary>
    /// <exception cref="ArgumentException">The query is empty or too long.</exception>
    public async Task<AccessListing> SearchAsync(long workspaceId, string? query, CancellationToken ct)
    {
        var text = query?.Trim() ?? string.Empty;
        if (text.Length == 0 || text.Length > MaxQueryLength)
        {
            throw new ArgumentException($"Enter between 1 and {MaxQueryLength} characters to search for.");
        }

        var (root, rules) = await OpenAsync(workspaceId, ct);
        return await Task.Run(() => Search(root, text, rules, ct), ct);
    }

    private async Task<(string Root, WorkspaceAccessRules Rules)> OpenAsync(long workspaceId, CancellationToken ct)
    {
        var workspace = (await workspaces.ListAsync(ct)).FirstOrDefault(item => item.Id == workspaceId)
            ?? throw new KeyNotFoundException("The workspace no longer exists.");
        return (Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace.Path)), await cache.GetAsync(workspaceId, ct));
    }

    private static string NormalizeFolder(string? folder)
    {
        var text = folder?.Trim() ?? string.Empty;
        if (text.Contains(':') || Path.IsPathRooted(text))
        {
            throw new ArgumentException("Use a folder inside the workspace.");
        }

        var segments = text.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(segment => segment != ".")
            .ToArray();
        return segments.Contains("..")
            ? throw new ArgumentException("Use a folder inside the workspace.")
            : string.Join('/', segments);
    }

    private static string ResolveFolder(string root, string relative)
    {
        var current = root;
        foreach (var segment in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            var info = new DirectoryInfo(current);
            if (!info.Exists)
            {
                throw new DirectoryNotFoundException($"The folder '{relative}' does not exist.");
            }

            if (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new ArgumentException("Links are not followed.");
            }
        }

        return current;
    }

    private AccessListing List(string root, string directory, WorkspaceAccessRules rules, CancellationToken ct)
    {
        var entries = new List<AccessEntry>();
        foreach (var info in new DirectoryInfo(directory).EnumerateFileSystemInfos("*", EntryOptions).Take(MaxScanned))
        {
            ct.ThrowIfCancellationRequested();
            entries.Add(ToEntry(root, info, rules));
        }

        var ordered = entries
            .OrderByDescending(entry => entry.IsFolder)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new AccessListing(ordered.Take(MaxEntries).ToList(), ordered.Count > MaxEntries);
    }

    private AccessListing Search(string root, string query, WorkspaceAccessRules rules, CancellationToken ct)
    {
        var isPattern = query.AsSpan().IndexOfAny('*', '?') >= 0;
        var results = new List<AccessEntry>();
        var scanned = 0;
        var truncated = false;
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0 && !truncated)
        {
            List<FileSystemInfo> children;
            try
            {
                children = new DirectoryInfo(pending.Pop()).EnumerateFileSystemInfos("*", EntryOptions).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var info in children)
            {
                ct.ThrowIfCancellationRequested();
                if (++scanned > MaxScanned)
                {
                    truncated = true;
                    break;
                }

                if (Matches(info.Name, query, isPattern))
                {
                    if (results.Count >= MaxResults)
                    {
                        truncated = true;
                        break;
                    }

                    results.Add(ToEntry(root, info, rules));
                }

                if (info is DirectoryInfo && !FileAccessPolicy.BaselineFolders.Contains(info.Name, StringComparer.OrdinalIgnoreCase))
                {
                    pending.Push(info.FullName);
                }
            }
        }

        return new AccessListing(results.OrderBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase).ToList(), truncated);
    }

    private static bool Matches(string name, string query, bool isPattern) =>
        isPattern
            ? FileSystemName.MatchesSimpleExpression(query, name, ignoreCase: true)
            : name.Contains(query, StringComparison.OrdinalIgnoreCase);

    // A folder is judged as a place to open files in: its own name is not run through the file-type check ('net10.0' is no file type).
    private AccessEntry ToEntry(string root, FileSystemInfo info, WorkspaceAccessRules rules)
    {
        var isFolder = info is DirectoryInfo;
        var relative = Path.GetRelativePath(root, info.FullName).Replace('\\', '/');
        var reason = policy.Evaluate(isFolder ? relative + "/_" : relative, rules);
        return new AccessEntry(relative, info.Name, isFolder, reason is null, reason);
    }
}
