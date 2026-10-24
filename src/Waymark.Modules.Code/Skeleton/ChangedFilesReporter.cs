using Waymark.Core.Security;
using Waymark.Core.Tools;

namespace Waymark.Modules.Code.Skeleton;

/// <summary>Lists the visible files modified after a cutoff, with size and line counts.</summary>
internal sealed class ChangedFilesReporter(FileAccessPolicy policy, SkeletonOptions options)
{
    public async Task AppendAsync(string root, DateTimeOffset cutoff, WorkspaceAccessRules rules, OutputBudget output, CancellationToken ct)
    {
        var cutoffUtc = cutoff.UtcDateTime;
        var (changed, truncated) = VisibleEntries.Scan(
            root,
            root,
            policy,
            rules,
            file => file.LastWriteTimeUtc > cutoffUtc,
            options.MaxScanEntries,
            options.MaxListedFiles,
            ct);

        if (changed.Count == 0)
        {
            output.TryAppendLine("**No files changed since the specified timestamp.**");
            return;
        }

        output.TryAppendLine($"**Total Changed Files:** {changed.Count}{(truncated ? " (list cut, see note below)" : string.Empty)}");
        output.TryAppendLine();
        output.TryAppendLine("## Changed Files");
        output.TryAppendLine("```");
        foreach (var file in changed)
        {
            var lines = await FileFacts.CountLinesAsync(file.Info.FullName, file.Info.Length, options.MaxLineCountBytes, ct).ConfigureAwait(false);
            var modified = FileFacts.FormatUtc(new DateTimeOffset(file.Info.LastWriteTimeUtc, TimeSpan.Zero));
            if (!output.TryAppendLine($"├── {file.RelativePath}"))
            {
                return;
            }

            output.TryAppendLine($"│   Modified: {modified} UTC {FileFacts.Describe(file.Info.Length, lines, options)}");
        }

        output.TryAppendLine("```");
        if (truncated)
        {
            output.TryAppendLine($"_More files changed than are listed (limit {options.MaxListedFiles}, or the workspace is very large). Use a later timestamp to narrow the list._");
        }
    }
}
