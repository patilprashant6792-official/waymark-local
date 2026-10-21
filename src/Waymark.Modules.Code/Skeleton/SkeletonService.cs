using System.Globalization;
using Waymark.Core.Security;
using Waymark.Core.Tools;
using Waymark.Core.Workspaces;

namespace Waymark.Modules.Code.Skeleton;

internal sealed class SkeletonService(
    IPathGuard guard,
    IWorkspaceStore workspaces,
    FileAccessPolicy policy,
    SkeletonOptions options,
    ToolPipelineOptions pipeline,
    TimeProvider time) : ISkeletonService
{
    private const string AllWorkspaces = "*";
    private const string FolderProbeName = "waymark-folder-probe";

    private readonly FolderTreeRenderer _tree = new(policy, options);
    private readonly ProjectFileSummarizer _projects = new(policy, options);
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

    public async Task<string> BuildSkeletonAsync(string? workspace, CancellationToken ct)
    {
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

        var root = ResolveRoot(found);
        var output = new OutputBudget(pipeline.MaxOutputChars);
        AppendHeader(output, "Workspace Skeleton", found);
        output.TryAppendLine("## Legend");
        output.TryAppendLine($"- ✓ **Small file** (≤{options.LargeFileBytes / 1024}KB) - Use `code_read`");
        output.TryAppendLine($"- ⚠️ **Large file** (>{options.LargeFileBytes / 1024}KB) - Use `code_outline` + `code_method`");
        output.TryAppendLine("- Files and folders that tools may not open (build output, version control, secrets, keys, unsupported file types) are not listed.");
        output.TryAppendLine();
        output.TryAppendLine("## Folder Structure");
        output.TryAppendLine("```");
        var cut = await _tree.RenderAsync(root, found.Name, output, ct).ConfigureAwait(false);
        output.TryAppendLine("```");
        if (cut)
        {
            output.TryAppendLine($"_Tree cut at {options.MaxTreeEntries} entries. Use code_folder_files to list a folder._");
        }

        output.TryAppendLine();
        await _projects.AppendAsync(root, output, ct).ConfigureAwait(false);
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
        await _changes.AppendAsync(root, cutoff, output, ct).ConfigureAwait(false);
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
        await _folders.AppendAsync(folder, requested, searchPattern, page, pageSize, output, ct).ConfigureAwait(false);
        return output.Build().TrimEnd();
    }

    // A folder name such as 'Waymark.Core' looks like a file with an unknown extension to the policy, so a child name is judged instead.
    private GuardedPath ResolveFolder(Workspace workspace, string requested)
    {
        var trimmed = requested.TrimEnd('/', '\\');
        if (trimmed.Length == 0 || trimmed == ".")
        {
            return guard.Resolve(workspace, string.Empty);
        }

        var probe = guard.Resolve(workspace, $"{trimmed}/{FolderProbeName}");
        var cut = probe.RelativePath.LastIndexOf('/');
        var relative = cut < 0 ? string.Empty : probe.RelativePath[..cut];
        return new GuardedPath(workspace, relative, Path.GetDirectoryName(probe.AbsolutePath)!);
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
