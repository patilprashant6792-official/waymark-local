using System.Globalization;
using Waymark.Core.Security;
using Waymark.Core.Tools;
using Waymark.Core.Workspaces;
using Waymark.Modules.Code.Content;

namespace Waymark.Modules.Code.Skeleton;

internal sealed class SkeletonService(
    IPathGuard guard,
    IWorkspaceStore workspaces,
    IAccessRuleCache accessRules,
    FileAccessPolicy policy,
    SkeletonOptions options,
    ContentOptions content,
    ToolPipelineOptions pipeline,
    TimeProvider time) : ISkeletonService
{
    private const string AllWorkspaces = "*";
    private const string FolderProbeName = "waymark-folder-probe";
    private const int MinTreeBudgetChars = 2_000;
    private const int NotesAllowanceChars = 800;
    private const int LargestClosedShown = 3;

    private static readonly EnumerationOptions CasingLookup = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint,
    };

    private readonly FolderScanner _scanner = new(policy, options, time);
    private readonly FolderTreeRenderer _tree = new(options);
    private readonly ProjectFileSummarizer _projects = new(policy, options);
    private readonly PythonProjectSummarizer _python = new(policy, options);
    private readonly ChangedFilesReporter _changes = new(policy, options);
    private readonly FolderFileLister _folders = new(policy, options);

    public async Task<string> ListWorkspacesAsync(CancellationToken ct)
    {
        var all = await workspaces.ListAsync(ct).ConfigureAwait(false);
        var output = new OutputBudget(pipeline.MaxOutputChars);
        output.TryAppendLine("# Workspaces");
        output.TryAppendLine();
        if (all.Count == 0)
        {
            output.TryAppendLine("No workspace is registered yet. Add a folder on the Workspaces page of the Waymark admin UI.");
            return output.Build().TrimEnd();
        }

        foreach (var workspace in all.OrderBy(w => w.Name, StringComparer.OrdinalIgnoreCase))
        {
            var indexed = workspace.LastIndexedUtc is { } at ? $"indexed {FileFacts.FormatUtc(at)} UTC" : "not indexed yet";
            var state = workspace.Enabled ? indexed : "disabled in the admin UI";
            output.TryAppendLine($"• {workspace.Name} - `{workspace.Path}` ({state})");
        }

        return output.Build().TrimEnd();
    }

    public async Task<string> BuildSkeletonAsync(string? workspace, SkeletonRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.Equals(workspace?.Trim(), AllWorkspaces, StringComparison.Ordinal))
        {
            return await ListWorkspacesAsync(ct).ConfigureAwait(false);
        }

        Workspace found;
        try
        {
            found = await guard.GetWorkspaceAsync(workspace, ct).ConfigureAwait(false);
        }
        catch (KeyNotFoundException ex)
        {
            return $"{ex.Message}\n\n{await ListWorkspacesAsync(ct).ConfigureAwait(false)}";
        }

        request.Validate(options);
        var rules = accessRules.Get(found.Id);
        var root = ResolveRoot(found);
        var requested = string.IsNullOrWhiteSpace(request.FolderPath) ? "." : request.FolderPath.Trim();
        var folder = ResolveExistingFolder(found, requested);
        var relative = folder.RelativePath.Length == 0 ? string.Empty : DiskCasedPath(root, folder.RelativePath);
        var rootLabel = relative.Length == 0 ? Path.GetFileName(root) : relative;
        // Scanning from the disk-cased path makes every path in the tree and its hints match the disk, whatever case was typed.
        var start = relative.Length == 0 ? root : Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

        var scan = _scanner.Scan(root, start, relative, rootLabel, rules, content.VendoredFolders, ScanTimeBudget(), ct);
        var projects = await _projects
            .RenderAsync(root, start, relative, found.Name, rules, request.IsOverview, pipeline.MaxOutputChars, ct)
            .ConfigureAwait(false);
        var python = await _python
            .RenderAsync(root, start, relative, found.Name, rules, request.IsOverview, pipeline.MaxOutputChars, ct)
            .ConfigureAwait(false);
        if (python.Length > 0)
        {
            projects = projects.Length == 0 ? python : projects + "\n\n" + python;
        }

        var output = new OutputBudget(pipeline.MaxOutputChars);
        AppendHeader(output, "Workspace Skeleton", found);
        if (relative.Length > 0)
        {
            output.TryAppendLine($"**Folder:** `{relative}` (up: {SkeletonCall(found.Name, ParentOf(relative))})");
        }

        output.TryAppendLine(request.Depth is { } depth
            ? string.Create(CultureInfo.InvariantCulture, $"**View:** {depth} level(s) below the folder")
            : "**View:** overview - folders open level by level until the size budget is used");
        output.TryAppendLine("## Legend");
        output.TryAppendLine($"- ✓ **Small file** (≤{options.LargeFileBytes / 1024}KB) - Use `code_read`");
        output.TryAppendLine($"- ⚠️ **Large file** (>{options.LargeFileBytes / 1024}KB) - Use `code_outline` + `code_method`");
        output.TryAppendLine($"- {FileFacts.ClosedMark} **Closed folder** - its summary in brackets; open it with `code_skeleton` and its `path`");
        output.TryAppendLine("- Files and folders that tools may not open (build output, version control, secrets, keys, unsupported file types, the workspace's access rules) are not listed or counted.");
        output.TryAppendLine();
        output.TryAppendLine("## Folder Structure");
        output.TryAppendLine("```");

        var plan = TreePlanner.Plan(
            scan.Root,
            rootLabel,
            new PlanSettings(TreeBudget(request, output.Length, projects.Length), request.Depth ?? options.MaxDepth, request.IsOverview, request.Page),
            options);
        var cut = await _tree.RenderAsync(scan.Root, rootLabel, plan, found.Name, output, ct).ConfigureAwait(false);
        output.TryAppendLine("```");
        if (cut)
        {
            output.TryAppendLine($"_Tree cut at {options.MaxTreeEntries} entries. Use code_folder_files to list a folder._");
        }

        AppendTreeNotes(output, plan, scan, found.Name, relative, rootLabel);
        output.TryAppendLine();
        foreach (var line in projects.Split('\n'))
        {
            output.TryAppendLine(line);
        }

        return output.Build().TrimEnd();
    }

    public async Task<string> ChangesSinceAsync(string? workspace, string? sinceTimestamp, CancellationToken ct)
    {
        var cutoff = ParseTimestamp(sinceTimestamp);
        var found = await guard.GetWorkspaceAsync(workspace, ct).ConfigureAwait(false);
        var root = ResolveRoot(found);
        var output = new OutputBudget(pipeline.MaxOutputChars);
        AppendHeader(output, $"Changed Files Since {sinceTimestamp!.Trim()}", found);
        output.TryAppendLine($"**Cutoff:** {FileFacts.FormatUtc(cutoff)} UTC");
        output.TryAppendLine();
        await _changes.AppendAsync(root, cutoff, accessRules.Get(found.Id), output, ct).ConfigureAwait(false);
        return output.Build().TrimEnd();
    }

    public async Task<string> ListFolderFilesAsync(
        string? workspace,
        string? folderPath,
        string? searchPattern,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        var found = await guard.GetWorkspaceAsync(workspace, ct).ConfigureAwait(false);
        var requested = string.IsNullOrWhiteSpace(folderPath) ? "." : folderPath.Trim();
        var folder = ResolveFolder(found, requested);
        var output = new OutputBudget(pipeline.MaxOutputChars);
        await _folders.AppendAsync(folder, requested, searchPattern, page, pageSize, accessRules.Get(found.Id), output, ct).ConfigureAwait(false);
        return output.Build().TrimEnd();
    }

    // The tree gets what the header, the notes and the project summary leave of the budget, but never less than a readable minimum.
    private int TreeBudget(SkeletonRequest request, int headerChars, int projectChars)
    {
        var total = request.IsOverview ? Math.Min(options.OverviewBudgetChars, pipeline.MaxOutputChars) : pipeline.MaxOutputChars;
        return Math.Max(MinTreeBudgetChars, total - headerChars - projectChars - NotesAllowanceChars);
    }

    // Half the call timeout at most, so a slow disk returns a partial tree instead of a timeout with nothing.
    private TimeSpan ScanTimeBudget()
    {
        var limit = TimeSpan.FromSeconds(options.ScanBudgetSeconds);
        var half = pipeline.CallTimeout / 2;
        return half < limit ? half : limit;
    }

    private void AppendTreeNotes(OutputBudget output, TreePlan plan, FolderScanResult scan, string workspaceName, string relative, string rootLabel)
    {
        var path = relative.Length == 0 ? "." : relative;
        if (plan.PageCount > 1)
        {
            var from = ((plan.Page - 1) * plan.PageSize) + 1;
            var to = from + plan.RootItems.Count - 1;
            var next = plan.Page < plan.PageCount
                ? string.Create(CultureInfo.InvariantCulture, $" Next: {SkeletonCall(workspaceName, path)} with page={plan.Page + 1}.")
                : string.Empty;
            output.TryAppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"_Entries {from}-{to} of {plan.RootItemTotal} in `{rootLabel}` (page {plan.Page}/{plan.PageCount}).{next}_"));
        }

        if (plan.Closed.Count > 0)
        {
            var largest = plan.Closed
                .Where(folder => folder.Scanned)
                .OrderByDescending(folder => folder.TotalFiles)
                .ThenBy(folder => folder.RelativePath, StringComparer.OrdinalIgnoreCase)
                .Take(LargestClosedShown)
                .ToList();
            var named = string.Join(", ", largest.Select(folder => string.Create(CultureInfo.InvariantCulture, $"`{folder.RelativePath}` ({folder.TotalFiles} files)")));
            var example = largest.Count > 0 ? largest[0].RelativePath : plan.Closed[0].RelativePath;
            var reason = plan.CutByBudget ? "the size budget" : "the depth or the overview rules (vendored code, very wide folders)";
            output.TryAppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"_{plan.Closed.Count} folder(s) marked {FileFacts.ClosedMark} are closed because of {reason}.{(named.Length > 0 ? $" Largest: {named}." : string.Empty)} Open one: {SkeletonCall(workspaceName, example)}._"));
        }

        if (scan.StopReason is { } stop)
        {
            output.TryAppendLine($"_The scan stopped at {stop}: totals marked ≥ are lower bounds and folders marked 'not scanned' were not read. Open a subfolder with its path to scan it in full._");
        }
    }

    private static string SkeletonCall(string workspaceName, string path) =>
        path == "."
            ? $"code_skeleton(\"{Escape(workspaceName)}\")"
            : $"code_skeleton(\"{Escape(workspaceName)}\", path=\"{Escape(path)}\")";

    private static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private static string ParentOf(string relative)
    {
        var cut = relative.LastIndexOf('/');
        return cut < 0 ? "." : relative[..cut];
    }

    /// <exception cref="ArgumentException">The path names a file.</exception>
    /// <exception cref="DirectoryNotFoundException">The folder does not exist.</exception>
    private GuardedPath ResolveExistingFolder(Workspace workspace, string requested)
    {
        var folder = ResolveFolder(workspace, requested);
        if (Directory.Exists(folder.AbsolutePath))
        {
            return folder;
        }

        if (File.Exists(folder.AbsolutePath))
        {
            throw new ArgumentException($"'{requested}' is a file, not a folder. Use code_outline for a C# or Python file, or code_read.");
        }

        throw new DirectoryNotFoundException($"Folder '{requested}' not found in workspace '{workspace.Name}'.");
    }

    // A folder name such as 'Waymark.Core' looks like a file with an unknown extension to the policy, so a child name is judged instead.
    // Leading and trailing separators are dropped, so '/src/' means the same folder as 'src'.
    private GuardedPath ResolveFolder(Workspace workspace, string requested)
    {
        var trimmed = requested.Trim('/', '\\');
        if (trimmed.Length == 0 || trimmed == ".")
        {
            return guard.Resolve(workspace, string.Empty);
        }

        GuardedPath probe;
        try
        {
            probe = guard.Resolve(workspace, $"{trimmed}/{FolderProbeName}");
        }
        catch (FileAccessDeniedException ex)
        {
            // The probe name is an internal detail; the refusal names the folder that was asked for.
            throw new FileAccessDeniedException(trimmed, ex.Reason);
        }
        var cut = probe.RelativePath.LastIndexOf('/');
        var relative = cut < 0 ? string.Empty : probe.RelativePath[..cut];
        return new GuardedPath(workspace, relative, Path.GetDirectoryName(probe.AbsolutePath)!);
    }

    // The path as typed may differ in letter case from the disk; the tree and its hints show the names as they are on disk.
    private static string DiskCasedPath(string root, string relative)
    {
        var current = new DirectoryInfo(root);
        var names = new List<string>();
        foreach (var segment in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            DirectoryInfo? exact = null;
            DirectoryInfo? caseless = null;
            try
            {
                foreach (var child in current.EnumerateDirectories("*", CasingLookup))
                {
                    if (child.Name.Equals(segment, StringComparison.Ordinal))
                    {
                        exact = child;
                        break;
                    }

                    caseless ??= child.Name.Equals(segment, StringComparison.OrdinalIgnoreCase) ? child : null;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return relative;
            }

            var match = exact ?? caseless;
            if (match is null)
            {
                return relative;
            }

            names.Add(match.Name);
            current = match;
        }

        return string.Join('/', names);
    }

    private void AppendHeader(OutputBudget output, string title, Workspace workspace)
    {
        output.TryAppendLine($"# {title}");
        output.TryAppendLine($"**Workspace:** {workspace.Name}");
        output.TryAppendLine($"**Path:** `{workspace.Path}`");
        output.TryAppendLine($"**Generated:** {FileFacts.FormatUtc(time.GetUtcNow())} UTC");
        if (workspace.LastIndexedUtc is { } indexed)
        {
            output.TryAppendLine($"**Last indexed:** {FileFacts.FormatUtc(indexed)} UTC");
        }
    }

    private static string ResolveRoot(Workspace workspace)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace.Path));
        return Directory.Exists(root)
            ? root
            : throw new DirectoryNotFoundException($"The folder of workspace '{workspace.Name}' no longer exists on disk.");
    }

    private static DateTimeOffset ParseTimestamp(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            throw new ArgumentException("sinceTimestamp is required: a Unix timestamp (e.g. '1705449600') or an ISO 8601 date (e.g. '2026-01-17T00:00:00Z').", nameof(value));
        }

        if (long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            try
            {
                return DateTimeOffset.FromUnixTimeSeconds(seconds);
            }
            catch (ArgumentOutOfRangeException)
            {
                throw new ArgumentException($"The Unix timestamp '{text}' is out of range.", nameof(value));
            }
        }

        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            return parsed;
        }

        throw new ArgumentException(
            $"Invalid timestamp format: '{text}'. Expected a Unix timestamp (e.g. '1705449600') or an ISO 8601 date (e.g. '2026-01-17T00:00:00Z').",
            nameof(value));
    }
}
