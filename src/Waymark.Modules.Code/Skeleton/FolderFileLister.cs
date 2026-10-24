using System.Globalization;
using Waymark.Core.Security;
using Waymark.Core.Tools;

namespace Waymark.Modules.Code.Skeleton;

/// <summary>Lists the visible files directly inside one folder, filtered by name and paged.</summary>
internal sealed class FolderFileLister(FileAccessPolicy policy, SkeletonOptions options)
{
    /// <exception cref="DirectoryNotFoundException">The folder does not exist.</exception>
    public async Task AppendAsync(
        GuardedPath folder,
        string requestedFolder,
        string? searchPattern,
        int page,
        int pageSize,
        WorkspaceAccessRules rules,
        OutputBudget output,
        CancellationToken ct)
    {
        if (!Directory.Exists(folder.AbsolutePath))
        {
            throw new DirectoryNotFoundException($"Folder '{requestedFolder}' not found in workspace '{folder.Workspace.Name}'.");
        }

        var pattern = searchPattern?.Trim();
        var size = pageSize < 1 ? options.DefaultPageSize : Math.Min(pageSize, options.MaxPageSize);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder.Workspace.Path));

        List<FileInfo> matches;
        try
        {
            matches = VisibleEntries.Files(root, folder.AbsolutePath, policy, rules)
                .Where(file => string.IsNullOrEmpty(pattern) || file.Name.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"Folder '{requestedFolder}' cannot be read.", ex);
        }

        var totalPages = (int)Math.Ceiling(matches.Count / (double)size);
        var current = Math.Max(page, 1);
        var paged = matches.Skip((current - 1) * size).Take(size).ToList();

        var filterLabel = string.IsNullOrEmpty(pattern) ? string.Empty : $" (filter: `{pattern}`)";
        output.TryAppendLine($"## Folder: `{requestedFolder}`{filterLabel}");
        output.TryAppendLine($"**Workspace:** {folder.Workspace.Name}");
        output.TryAppendLine();
        output.TryAppendLine(string.Create(CultureInfo.InvariantCulture, $"Showing {paged.Count} of {matches.Count} file(s) - page {current}/{Math.Max(totalPages, 1)}"));
        output.TryAppendLine();

        if (paged.Count == 0)
        {
            output.TryAppendLine(matches.Count == 0 ? "_No files found._" : $"_Page {current} is past the last page ({totalPages})._");
            return;
        }

        output.TryAppendLine("## Legend");
        output.TryAppendLine($"- ✓ **Small file** (≤{options.LargeFileBytes / 1024}KB) - Use `code_read`");
        output.TryAppendLine($"- ⚠️ **Large file** (>{options.LargeFileBytes / 1024}KB) - Use `code_outline` + `code_method`");
        output.TryAppendLine();

        foreach (var file in paged)
        {
            var lines = await FileFacts.CountLinesAsync(file.FullName, file.Length, options.MaxLineCountBytes, ct).ConfigureAwait(false);
            var relative = string.IsNullOrEmpty(folder.RelativePath) ? file.Name : $"{folder.RelativePath}/{file.Name}";
            var lineText = lines is null ? string.Empty : string.Create(CultureInfo.InvariantCulture, $", {lines} lines");
            output.TryAppendLine($"- {FileFacts.SizeMark(file.Length, options)} `{relative}` ({FileFacts.FormatSize(file.Length)}{lineText})");
        }

        if (current < totalPages)
        {
            output.TryAppendLine();
            output.TryAppendLine($"_Next page: call `code_folder_files` with `page={current + 1}`_");
        }

        if (current > 1)
        {
            output.TryAppendLine($"_Previous page: `page={current - 1}`_");
        }
    }
}
