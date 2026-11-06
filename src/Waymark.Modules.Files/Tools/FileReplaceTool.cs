using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Waymark.Core.Memory;
using Waymark.Core.Tools;
using Waymark.Modules.Files.Replace;

namespace Waymark.Modules.Files.Tools;

[McpServerToolType]
public sealed class FileReplaceTool(IReplaceService replacer, ToolPipelineOptions pipeline, IFlowEditAdvisor advisor)
{
    private const string LiteralMode = "literal";
    private const string CSharpMode = "csharp";
    private const int MaxCountedFiles = 30;

    [McpServerTool(Name = "file_replace", ReadOnly = false, Destructive = true, Idempotent = false)]
    [ToolGate(ToolGate.Confirm)]
    [Description(
        "Find and replace text across the files of one workspace, in two calls.\n" +
        "1. PREVIEW (no planId): reads and matches, writes nothing. Shows every match with its line, the count, and a planId (a preview without a planId shows only counts per file).\n" +
        "2. APPLY: the SAME arguments plus planId and confirmCount (the count after exclusions). Writes exactly the previewed matches in every file, or nothing.\n" +
        "Only the matched characters change: encoding, byte order mark and every line ending stay as they were.\n\n" +
        "mode 'literal' (default): find is plain text on one line; caseSensitive (default true), wholeWord (default false).\n" +
        "mode 'csharp': find and with are C# identifiers; every identifier token with that name in .cs files is renamed by syntax (strings and comments never). " +
        "Same-named members of other types change too: check the preview and drop wrong lines with excludeJson.\n\n" +
        "Scope: pathGlob ('**/*.cs', 'src/Orders/**', '**' for everything) or pathsJson (exact files, e.g. from code_grep), exactly one of them. " +
        "The access rules apply; vendored, minified, binary and files over 1 MB are left out and counted.\n" +
        "A plan lasts 10 minutes and is used once. A wrong confirmCount or different arguments are refused and the plan stays usable; " +
        "a planned file that changed after the preview is refused too, and that plan is used up: preview again. " +
        "A preview whose scan was partial (too many files, too slow, over the limits) gives no planId: narrow the scope.\n" +
        "RETURNS: preview - matches per file with marked lines and the planId; apply - replacements per file.")]
    public async Task<string> Replace(
        [Description("Workspace name as shown in Waymark (a single workspace; '*' is not accepted)")]
        string workspace,
        [Description("Text to find: one line, at most 200 characters. In mode 'csharp', an identifier.")]
        string find,
        [Description("Replacement: one line, at most 1000 characters; an empty string deletes the matches. In mode 'csharp', an identifier.")]
        string with,
        [Description("Files to search, relative to the workspace: '**/*.cs', 'src/Orders/**', '*.json', '**' for all. Give this or pathsJson.")]
        string? pathGlob = null,
        [Description("JSON array of exact workspace-relative file paths, at most 200: [\"src/A.cs\",\"docs/a.md\"]. Give this or pathGlob.")]
        string? pathsJson = null,
        [Description("'literal' (default) or 'csharp'")]
        string? mode = null,
        [Description("Literal mode: match letter case exactly (default true).")]
        bool caseSensitive = true,
        [Description("Literal mode: a match must not touch a letter, digit or '_' (default false).")]
        bool wholeWord = false,
        [Description("Leave out to preview. To apply: the planId the preview returned.")]
        string? planId = null,
        [Description("Apply only, required: the number of replacements after exclusions, as the preview counted them.")]
        int? confirmCount = null,
        [Description("Apply only: JSON array of matches to leave alone: \"path\" (the whole file) or \"path#L12\" (every match on line 12).")]
        string? excludeJson = null,
        CancellationToken cancellationToken = default)
    {
        var request = new ReplaceRequest(
            workspace,
            find,
            with,
            pathGlob,
            ParseList(pathsJson, nameof(pathsJson)),
            ParseMode(mode),
            caseSensitive,
            wholeWord);

        try
        {
            if (string.IsNullOrWhiteSpace(planId))
            {
                if (confirmCount is not null || !string.IsNullOrWhiteSpace(excludeJson))
                {
                    throw new McpException("confirmCount and excludeJson belong to the apply call, which also needs planId. Preview first without them.");
                }

                var started = Stopwatch.GetTimestamp();
                var preview = await replacer.PreviewAsync(request, cancellationToken).ConfigureAwait(false);
                return FormatPreview(request, preview, Stopwatch.GetElapsedTime(started));
            }

            if (confirmCount is not { } count)
            {
                throw new McpException("confirmCount is required with planId: the number of replacements the preview showed, after exclusions.");
            }

            var exclusions = ParseList(excludeJson, nameof(excludeJson)) ?? [];
            var result = await replacer.ApplyAsync(request, planId, count, exclusions, cancellationToken).ConfigureAwait(false);
            var text = FormatApply(result);
            if (!result.Applied)
            {
                return text;
            }

            // Tells the agent when the change touched a step of a saved flow and a link no longer holds. Silent for almost every change.
            var advice = await advisor.AdviseAsync(workspace, [.. result.Files.Select(file => file.RelativePath)], FlowEditKind.Edited, cancellationToken).ConfigureAwait(false);
            return advice is null ? text : $"{text}\n{advice}";
        }
        catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException or InvalidOperationException)
        {
            throw new McpException(ex.Message);
        }
    }

    private static ReplaceMode ParseMode(string? mode)
    {
        var value = mode?.Trim();
        if (string.IsNullOrEmpty(value) || string.Equals(value, LiteralMode, StringComparison.OrdinalIgnoreCase))
        {
            return ReplaceMode.Literal;
        }

        return string.Equals(value, CSharpMode, StringComparison.OrdinalIgnoreCase)
            ? ReplaceMode.CSharp
            : throw new McpException($"mode must be '{LiteralMode}' or '{CSharpMode}'.");
    }

    private static List<string>? ParseList(string? json, string name)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var items = JsonSerializer.Deserialize<List<string>>(json);
            return items is { Count: > 0 } && items.TrueForAll(item => item is not null)
                ? items
                : throw new McpException($"{name} must be a non-empty JSON array of strings.");
        }
        catch (JsonException ex)
        {
            throw new McpException($"{name} is not valid: {ex.Message}");
        }
    }

    private string FormatPreview(ReplaceRequest request, ReplacePreview preview, TimeSpan elapsed)
    {
        var output = new OutputBudget(pipeline.MaxOutputChars);
        output.TryAppendLine("🔍 file_replace preview - nothing was written");
        output.TryAppendLine(Describe(request));
        var took = elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture);
        if (preview.Summary.Read == 0 && preview.Files.Count == 0 && preview.Refused.Count > 0)
        {
            output.TryAppendLine($"Nothing was read: none of the named files can be used (listed below). Took {took} s.");
        }
        else
        {
            output.TryAppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{preview.MatchCount} match(es) in {preview.Files.Count} file(s). {Coverage(preview.Summary)} Took {took} s."));
        }

        if (preview.PlanId is { } id)
        {
            var until = preview.ExpiresUtc?.ToString("HH:mm:ss", CultureInfo.InvariantCulture) ?? "?";
            output.TryAppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"✅ Plan {id}, valid until {until} UTC, single use. To apply: the same arguments plus planId='{id}' and confirmCount={preview.MatchCount} (lower it by the matches you exclude with excludeJson)."));
        }
        else
        {
            output.TryAppendLine("⛔ No plan, nothing can be applied:");
            foreach (var blocker in preview.Blockers)
            {
                output.TryAppendLine($"   - {blocker}");
            }
        }

        foreach (var refused in preview.Refused)
        {
            output.TryAppendLine($"   ❌ {refused.RelativePath}: {refused.Reason}");
        }

        if (preview.PlanId is null)
        {
            // Without a plan nothing can be applied, so the lines would only fill the context; the counts show where to narrow the scope.
            AppendCounts(output, preview.Files);
            return output.Build().TrimEnd();
        }

        foreach (var file in preview.Files)
        {
            output.TryAppendLine();
            var errors = file.SyntaxErrors > 0
                ? string.Create(CultureInfo.InvariantCulture, $", {file.SyntaxErrors} syntax error(s) already in the file")
                : string.Empty;
            if (!output.TryAppendLine(string.Create(CultureInfo.InvariantCulture, $"{file.RelativePath} ({file.Matches}{errors})")))
            {
                break;
            }

            foreach (var line in file.Lines)
            {
                output.TryAppendLine(string.Create(CultureInfo.InvariantCulture, $"  {line.Number}: {line.Text}"));
            }

            if (file.MoreLines > 0)
            {
                output.TryAppendLine(string.Create(CultureInfo.InvariantCulture, $"  … and {file.MoreLines} more line(s) with matches"));
            }
        }

        return output.Build().TrimEnd();
    }

    private static void AppendCounts(OutputBudget output, IReadOnlyList<PreviewFile> files)
    {
        if (files.Count == 0)
        {
            return;
        }

        output.TryAppendLine();
        output.TryAppendLine("Matches per file, most first (lines are shown once a preview gives a plan):");
        foreach (var file in files.OrderByDescending(f => f.Matches).ThenBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase).Take(MaxCountedFiles))
        {
            output.TryAppendLine(string.Create(CultureInfo.InvariantCulture, $"   {file.RelativePath} ({file.Matches})"));
        }

        if (files.Count > MaxCountedFiles)
        {
            output.TryAppendLine(string.Create(CultureInfo.InvariantCulture, $"   … and {files.Count - MaxCountedFiles} more file(s)"));
        }
    }

    private string FormatApply(ReplaceApplyResult result)
    {
        var output = new OutputBudget(pipeline.MaxOutputChars);
        if (!result.Applied)
        {
            output.TryAppendLine("⚠️ file_replace refused - nothing was written");
            output.TryAppendLine($"   ❌ {result.Refusal}");
            return output.Build().TrimEnd();
        }

        var total = result.Files.Sum(file => file.Replacements);
        var excluded = result.Excluded > 0 ? string.Create(CultureInfo.InvariantCulture, $", {result.Excluded} excluded") : string.Empty;
        output.TryAppendLine(string.Create(CultureInfo.InvariantCulture, $"✅ file_replace - {total} replacement(s) in {result.Files.Count} file(s){excluded}"));
        foreach (var file in result.Files)
        {
            output.TryAppendLine(string.Create(CultureInfo.InvariantCulture, $"   ✅ {file.RelativePath} ({file.Replacements})"));
        }

        output.TryAppendLine("Undo: cmd_git restore with these paths, if the workspace is a git repository.");
        return output.Build().TrimEnd();
    }

    private static string Describe(ReplaceRequest request)
    {
        var parts = new List<string> { $"'{request.Find}' → '{request.With}'" };
        if (request.Mode == ReplaceMode.CSharp)
        {
            parts.Add("C# identifiers");
        }
        else
        {
            parts.Add("literal");
            parts.Add(request.CaseSensitive ? "case-sensitive" : "any case");
            if (request.WholeWord)
            {
                parts.Add("whole word");
            }
        }

        parts.Add(request.PathGlob is { Length: > 0 } glob
            ? $"glob '{glob.Trim()}'"
            : string.Create(CultureInfo.InvariantCulture, $"{request.Paths?.Count ?? 0} named file(s)"));
        return string.Join(" · ", parts);
    }

    private static string Coverage(ScanSummary summary)
    {
        var left = new List<string>();
        Add(left, summary.TooLarge, "too large");
        Add(left, summary.VendoredOrMinified, "vendored or minified");
        Add(left, summary.NotText, "not UTF-8/UTF-16 text");
        Add(left, summary.Unreadable, "unreadable");
        Add(left, summary.NotCSharp, "not C#");
        Add(left, summary.Unscanned, "not read in time");
        var notRead = left.Count == 0 ? "nothing" : string.Join(", ", left);
        return string.Create(CultureInfo.InvariantCulture, $"Read {summary.Read} file(s); left out: {notRead}.");
    }

    private static void Add(List<string> parts, int count, string reason)
    {
        if (count > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{count} {reason}"));
        }
    }
}
