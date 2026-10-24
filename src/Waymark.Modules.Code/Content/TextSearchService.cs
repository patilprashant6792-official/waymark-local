using System.Globalization;
using Waymark.Core.Security;
using Waymark.Core.Tools;
using Waymark.Core.Text;
using Waymark.Core.Workspaces;
using Waymark.Modules.Code.Reading;

namespace Waymark.Modules.Code.Content;

/// <summary>
/// code_grep: reads the files from disk on every call, so a result can never be older than the file it points to and
/// its line numbers are the ones code_read and file_edit_lines use. Files are read in path-ordered batches so paging is stable.
/// </summary>
internal sealed class TextSearchService(
    IPathGuard guard,
    IWorkspaceStore workspaces,
    IAccessRuleCache ruleCache,
    FileAccessPolicy policy,
    ContentOptions options,
    ToolPipelineOptions pipeline) : ITextSearchService
{
    private const string AllWorkspaces = "*";
    private const int BatchSize = 64;
    private const int Parallelism = 4;
    private const string Ellipsis = "…";

    public async Task<string> GrepAsync(string? workspace, string? query, bool caseSensitive, string? pathGlob, int? pageSize, int? page, CancellationToken ct)
    {
        var text = ValidateQuery(query);
        var glob = PathGlob.Parse(pathGlob);
        var searched = await ResolveWorkspacesAsync(workspace, ct).ConfigureAwait(false);
        var report = new TextSearchReport(text, caseSensitive, [.. searched.Select(w => w.Name)]);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.ScanBudgetSeconds));
        try
        {
            var targets = await ListTargetsAsync(searched, glob, report, deadline.Token).ConfigureAwait(false);
            await ScanAsync(targets, report, deadline.Token, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            report.BudgetExceeded = true;
        }

        return Render(report, pageSize, page);
    }

    private string ValidateQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new ArgumentException("query is required.");
        }

        if (query.Length > options.MaxQueryLength)
        {
            throw new ArgumentException($"query is longer than {options.MaxQueryLength} characters.");
        }

        return query.Contains('\n') || query.Contains('\r')
            ? throw new ArgumentException("query must be a single line; matching works line by line.")
            : query;
    }

    private async Task<IReadOnlyList<Workspace>> ResolveWorkspacesAsync(string? workspace, CancellationToken ct)
    {
        if (!string.Equals(workspace?.Trim(), AllWorkspaces, StringComparison.Ordinal))
        {
            return [await guard.GetWorkspaceAsync(workspace, ct).ConfigureAwait(false)];
        }

        var enabled = (await workspaces.ListAsync(ct).ConfigureAwait(false))
            .Where(w => w.Enabled)
            .OrderBy(w => w.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return enabled.Count > 0 ? enabled : throw new ArgumentException("No enabled workspace exists. Add one in Waymark first.");
    }

    private async Task<List<ScanTarget>> ListTargetsAsync(IReadOnlyList<Workspace> searched, PathGlob? glob, TextSearchReport report, CancellationToken token)
    {
        var targets = new List<ScanTarget>();
        foreach (var workspace in searched)
        {
            if (!Directory.Exists(workspace.Path))
            {
                report.UnreachableWorkspaces.Add(workspace.Name);
                continue;
            }

            var rules = await ruleCache.GetAsync(workspace.Id, token).ConfigureAwait(false);
            var walk = await Task.Run(
                () => ContentFileWalker.Enumerate(workspace.Path, policy, rules, options, glob, token),
                token).ConfigureAwait(false);

            report.Coverage.Add(walk.Coverage);
            if (walk.Truncated)
            {
                report.TruncatedWorkspaces.Add(workspace.Name);
            }

            targets.AddRange(walk.Files.Select(file => new ScanTarget(workspace.Name, file)));
        }

        return targets;
    }

    // Reads batch after batch in path order. A batch is applied in order and only up to its first unfinished file, so a
    // stop (time budget, match limit) always leaves a clean prefix of the file list and the same query pages the same way.
    private async Task ScanAsync(List<ScanTarget> targets, TextSearchReport report, CancellationToken deadlineToken, CancellationToken ct)
    {
        var comparison = report.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = Math.Min(Parallelism, Environment.ProcessorCount), CancellationToken = deadlineToken };

        for (var start = 0; start < targets.Count; start += BatchSize)
        {
            var batch = targets.GetRange(start, Math.Min(BatchSize, targets.Count - start));
            var results = new FileScan?[batch.Count];
            try
            {
                await Parallel.ForEachAsync(
                    Enumerable.Range(0, batch.Count),
                    parallel,
                    async (index, token) => results[index] = await ScanFileAsync(batch[index].File, report.Query, comparison, token).ConfigureAwait(false))
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // The time budget ran out: keep the files that finished.
            }

            for (var index = 0; index < batch.Count; index++)
            {
                if (results[index] is not { } scan)
                {
                    report.BudgetExceeded = true;
                    report.UnscannedFiles = targets.Count - (start + index);
                    return;
                }

                Apply(report, batch[index], scan);
                if (report.Matches.Count >= options.MaxMatchingFiles && scan.MatchingLines > 0)
                {
                    report.UnscannedFiles = targets.Count - (start + index + 1);
                    report.MatchLimitReached = report.UnscannedFiles > 0;
                    return;
                }
            }
        }
    }

    private static void Apply(TextSearchReport report, ScanTarget target, FileScan scan)
    {
        switch (scan.Outcome)
        {
            case ScanOutcome.Scanned:
                report.Coverage.Scanned++;
                if (scan.MatchingLines > 0)
                {
                    report.Matches.Add(new FileHits(target.Workspace, target.File.RelativePath, scan.MatchingLines, scan.Lines));
                }

                break;
            case ScanOutcome.Binary:
                report.Coverage.Binary++;
                break;
            case ScanOutcome.Minified:
                report.Coverage.VendoredOrMinified++;
                break;
            default:
                report.Coverage.Unreadable++;
                break;
        }
    }

    private async Task<FileScan> ScanFileAsync(ContentFile file, string query, StringComparison comparison, CancellationToken ct)
    {
        LoadedText loaded;
        try
        {
            loaded = await LoadedText.LoadAsync(file.AbsolutePath, options.MaxFileBytes, ct).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            return FileScan.Skipped(ScanOutcome.Binary);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return FileScan.Skipped(ScanOutcome.Unreadable);
        }

        return ContentEligibility.LooksMinified(file.Size, loaded.LineCount, options)
            ? FileScan.Skipped(ScanOutcome.Minified)
            : FindMatches(loaded, query, comparison);
    }

    private FileScan FindMatches(LoadedText loaded, string query, StringComparison comparison)
    {
        var full = loaded.Text.ToString();
        if (!full.Contains(query, comparison))
        {
            return new FileScan(ScanOutcome.Scanned, 0, []);
        }

        var shown = new List<MatchedLine>();
        var total = 0;
        foreach (var line in loaded.Text.Lines)
        {
            var span = full.AsSpan(line.Start, line.Span.Length);
            var at = span.IndexOf(query, comparison);
            if (at < 0)
            {
                continue;
            }

            total++;
            if (shown.Count < options.MaxLinesPerFile)
            {
                shown.Add(new MatchedLine(line.LineNumber + 1, Excerpt(span, at, options.MaxDisplayedLineChars)));
            }
        }

        return new FileScan(ScanOutcome.Scanned, total, shown);
    }

    // Leading and trailing blanks are dropped; a long line is cut to a window around the match so the match stays visible.
    private static string Excerpt(ReadOnlySpan<char> line, int matchIndex, int maxChars)
    {
        var lead = line.Length - line.TrimStart().Length;
        var trimmed = line[lead..].TrimEnd();
        if (trimmed.Length <= maxChars)
        {
            return trimmed.ToString();
        }

        var start = Math.Clamp(Math.Max(0, matchIndex - lead) - (maxChars / 4), 0, trimmed.Length - maxChars);
        var window = trimmed.Slice(start, maxChars).ToString();
        return (start > 0 ? Ellipsis : string.Empty) + window + (start + maxChars < trimmed.Length ? Ellipsis : string.Empty);
    }

    private string Render(TextSearchReport report, int? requestedPageSize, int? requestedPage)
    {
        var rows = Flatten(report.Matches);
        var size = Math.Clamp(requestedPageSize ?? options.PageSize, 1, options.MaxPageSize);
        var pages = Math.Max(1, (int)Math.Ceiling(rows.Count / (double)size));
        var page = Math.Clamp(requestedPage ?? 1, 1, pages);
        var showWorkspace = report.Workspaces.Count > 1;
        var totalLines = report.Matches.Sum(m => m.MatchingLines);

        var output = new OutputBudget(pipeline.MaxOutputChars);
        output.TryAppendLine($"# Text search: \"{report.Query}\"");
        output.TryAppendLine();
        output.TryAppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"**Workspaces searched:** {string.Join(", ", report.Workspaces)} | **Files with matches:** {report.Matches.Count} | **Matching lines:** {totalLines} | **Page:** {page} of {pages} ({size} per page)"));
        output.TryAppendLine(report.CaseSensitive
            ? "_Literal text, case-sensitive, one line at a time, in path order. Read from disk just now._"
            : "_Literal text, any letter case, one line at a time, in path order. Read from disk just now._");
        foreach (var note in Notes(report))
        {
            output.TryAppendLine(note);
        }

        output.TryAppendLine();
        if (rows.Count == 0)
        {
            output.TryAppendLine("No matches.");
            return output.Build().TrimEnd();
        }

        foreach (var group in rows.Skip((page - 1) * size).Take(size).GroupBy(row => row.File))
        {
            var file = group.Key;
            var shown = group.Where(row => row.Line is not null).Select(row => row.Line!).ToList();
            output.TryAppendLine(HeadingOf(file, showWorkspace ? $"{file.Workspace}: {file.Path}" : file.Path, shown));
            foreach (var row in group)
            {
                output.TryAppendLine(row.Line is { } line
                    ? string.Create(CultureInfo.InvariantCulture, $"  {line.Number}: {line.Text}")
                    : string.Create(CultureInfo.InvariantCulture, $"  ... {file.MatchingLines - file.Lines.Count} more matching {(file.MatchingLines - file.Lines.Count == 1 ? "line" : "lines")} in this file; use a longer query or read the file with code_read."));
            }
        }

        if (page < pages)
        {
            output.TryAppendLine();
            output.TryAppendLine($"More results: call again with page={page + 1}.");
        }

        return output.Build().TrimEnd();
    }

    private static string CountLines(int count) => count == 1 ? "1 matching line" : string.Create(CultureInfo.InvariantCulture, $"{count} matching lines");

    // A file whose lines continue on another page says which of its matches this page shows.
    private static string HeadingOf(FileHits file, string where, IReadOnlyList<MatchedLine> shown)
    {
        if (shown.Count == 0 || shown.Count == file.MatchingLines)
        {
            return string.Create(CultureInfo.InvariantCulture, $"**{where}** ({CountLines(file.MatchingLines)})");
        }

        var first = file.Lines.ToList().IndexOf(shown[0]) + 1;
        var range = shown.Count == 1
            ? string.Create(CultureInfo.InvariantCulture, $"match {first}")
            : string.Create(CultureInfo.InvariantCulture, $"matches {first}-{first + shown.Count - 1}");
        return string.Create(CultureInfo.InvariantCulture, $"**{where}** (showing {range} of {file.MatchingLines})");
    }
    private static List<Row> Flatten(List<FileHits> matches)
    {
        var rows = new List<Row>();
        foreach (var match in matches)
        {
            rows.AddRange(match.Lines.Select(line => new Row(match, line)));
            if (match.MatchingLines > match.Lines.Count)
            {
                rows.Add(new Row(match, null));
            }
        }

        return rows;
    }

    private IEnumerable<string> Notes(TextSearchReport report)
    {
        var coverage = report.Coverage;
        var left = new List<string>();
        AddIf(left, coverage.TooLarge, $"over {FormatSize(options.MaxFileBytes)}");
        AddIf(left, coverage.VendoredOrMinified, "vendored or minified");
        AddIf(left, coverage.Binary, "binary");
        AddIf(left, coverage.Unreadable, "unreadable");
        var notRead = left.Count == 0 ? "nothing else" : string.Join(", ", left);
        yield return string.Create(
            CultureInfo.InvariantCulture,
            $"_Read {coverage.Scanned} files. Not read: {notRead}. Files the access policy blocks are never searched._");

        foreach (var name in report.UnreachableWorkspaces)
        {
            yield return $"_Workspace '{name}' folder was not found on disk and was not searched._";
        }

        foreach (var name in report.TruncatedWorkspaces)
        {
            yield return string.Create(
                CultureInfo.InvariantCulture,
                $"_Workspace '{name}' has more than {options.MaxFilesPerWorkspace} searchable files; only part of it was searched. Use pathGlob to target a folder._");
        }

        if (report.BudgetExceeded)
        {
            yield return string.Create(
                CultureInfo.InvariantCulture,
                $"_Stopped after {options.ScanBudgetSeconds} seconds; {report.UnscannedFiles} files were not read, so results are partial. Narrow with pathGlob or a single workspace._");
        }

        if (report.MatchLimitReached)
        {
            yield return string.Create(
                CultureInfo.InvariantCulture,
                $"_Stopped after {options.MaxMatchingFiles} files with matches; {report.UnscannedFiles} files were not read. Use a longer query or pathGlob._");
        }
    }

    private static void AddIf(List<string> target, int count, string reason)
    {
        if (count > 0)
        {
            target.Add(string.Create(CultureInfo.InvariantCulture, $"{count} {reason}"));
        }
    }

    private static string FormatSize(int bytes) =>
        bytes >= 1_048_576
            ? string.Create(CultureInfo.InvariantCulture, $"{bytes / 1_048_576.0:0.##} MB")
            : string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0.#} KB");

    private sealed record ScanTarget(string Workspace, ContentFile File);

    private sealed record Row(FileHits File, MatchedLine? Line);
}
