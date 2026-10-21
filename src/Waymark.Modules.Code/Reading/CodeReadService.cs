using Waymark.Core.Code;
using Waymark.Core.Security;
using Waymark.Core.Tools;
using Waymark.Core.Workspaces;

namespace Waymark.Modules.Code.Reading;

internal sealed class CodeReadService(
    IPathGuard guard,
    ISyntaxIndexer indexer,
    CodeReadOptions options,
    ToolPipelineOptions pipeline) : ICodeReadService
{
    private const char ListSeparator = ',';
    private const int MaxListedMemberNames = 40;

    public async Task<string> OutlineAsync(string? workspace, string? relativeFilePaths, bool includePrivateMembers, CancellationToken ct)
    {
        var paths = SplitList(relativeFilePaths, "relativeFilePath", options.MaxOutlineFiles);
        var found = await guard.GetWorkspaceAsync(workspace, ct).ConfigureAwait(false);
        var parsed = new List<(CodeFileIndex File, bool Truncated)>();
        var warnings = new List<string>();
        foreach (var path in paths)
        {
            try
            {
                parsed.Add(await ParseAsync(found, path, ct).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is FileAccessDeniedException or ArgumentException or IOException or UnauthorizedAccessException)
            {
                warnings.Add($"{path}: {ex.Message}");
            }
        }

        if (parsed.Count == 0)
        {
            throw new ArgumentException($"No file could be analyzed.\n{string.Join('\n', warnings.Select(w => "- " + w))}");
        }

        var output = new OutputBudget(pipeline.MaxOutputChars);
        if (paths.Count > 1)
        {
            AppendBatchHeader(output, found.Name, parsed, warnings);
        }

        for (var i = 0; i < parsed.Count; i++)
        {
            if (i > 0)
            {
                output.TryAppendLine("---");
                output.TryAppendLine();
            }

            OutlineRenderer.Render(output, found.Name, parsed[i].File, includePrivateMembers, parsed[i].Truncated);
        }

        return output.Build().TrimEnd();
    }

    public async Task<string> MethodsAsync(string? workspace, string? relativeFilePath, string? methodNames, string? className, CancellationToken ct)
    {
        var names = SplitList(methodNames, "methodName", options.MaxMethodsPerCall);
        var found = await guard.GetWorkspaceAsync(workspace, ct).ConfigureAwait(false);
        var (file, _) = await ParseAsync(found, relativeFilePath, ct).ConfigureAwait(false);
        var source = await LoadMatchingAsync(found, file, ct).ConfigureAwait(false);

        var wantedClass = string.IsNullOrWhiteSpace(className) ? null : className.Trim();
        var candidates = file.Members
            .Where(m => m.TypeIndex is not null
                && (wantedClass is null || TypeMatches(file.Types[m.TypeIndex.Value], wantedClass)))
            .ToList();

        var selected = new List<CodeMemberEntry>();
        var notFound = new List<string>();
        foreach (var name in names)
        {
            var matches = candidates.Where(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 0)
            {
                notFound.Add(name);
            }

            selected.AddRange(matches.Where(m => !selected.Contains(m)));
        }

        var available = candidates.Select(m => m.Name).Distinct(StringComparer.Ordinal).Take(MaxListedMemberNames).ToList();
        if (selected.Count == 0)
        {
            var scope = wantedClass is null ? "the file" : $"class '{wantedClass}'";
            throw new ArgumentException(
                $"Not found in {scope}: {string.Join(", ", notFound)}. Members available: {(available.Count == 0 ? "(none)" : string.Join(", ", available))}.");
        }

        var output = new OutputBudget(pipeline.MaxOutputChars);
        MethodRenderer.Render(output, found.Name, file, source, selected, notFound, available);
        return output.Build().TrimEnd();
    }

    public async Task<string> ReadAsync(string? workspace, string? relativeFilePath, int? startLine, int? endLine, string? query, CancellationToken ct)
    {
        var hasQuery = !string.IsNullOrEmpty(query);
        if (hasQuery && (startLine is not null || endLine is not null))
        {
            throw new ArgumentException("'query' and 'startLine'/'endLine' are mutually exclusive. Use one mode at a time.");
        }

        if ((startLine is null) != (endLine is null))
        {
            throw new ArgumentException("'startLine' and 'endLine' must both be provided together.");
        }

        var target = await guard.ResolveAsync(workspace, relativeFilePath, ct).ConfigureAwait(false);
        var source = await LoadAsync(target, ct).ConfigureAwait(false);
        var output = new OutputBudget(pipeline.MaxOutputChars);
        if (hasQuery)
        {
            LineReader.AppendMatches(output, source, target.RelativePath, query!, options.MaxMatchLines);
        }
        else if (startLine is { } start)
        {
            LineReader.AppendRange(output, source, target.RelativePath, start, endLine!.Value);
        }
        else
        {
            LineReader.AppendAll(output, source, target.RelativePath);
        }

        return output.Build().TrimEnd();
    }

    private static void AppendBatchHeader(
        OutputBudget output,
        string workspace,
        List<(CodeFileIndex File, bool Truncated)> parsed,
        List<string> warnings)
    {
        output.TryAppendLine($"# Batch C# File Analysis: {parsed.Count} file(s)");
        output.TryAppendLine();
        output.TryAppendLine($"**Workspace:** {workspace}");
        output.TryAppendLine();
        if (warnings.Count > 0)
        {
            output.TryAppendLine("⚠️ **Warnings:**");
            foreach (var warning in warnings)
            {
                output.TryAppendLine($"- {warning}");
            }

            output.TryAppendLine();
        }

        output.TryAppendLine("## Files Index");
        output.TryAppendLine();
        for (var i = 0; i < parsed.Count; i++)
        {
            var file = parsed[i].File;
            var methods = file.Members.Count(m => m.Kind is CodeMemberKind.Method or CodeMemberKind.Constructor);
            output.TryAppendLine($"{i + 1}. **{Path.GetFileName(file.Stamp.RelativePath)}** → {file.Types.Count} type(s), {methods} method(s)");
        }

        output.TryAppendLine();
        output.TryAppendLine("---");
        output.TryAppendLine();
    }

    private static bool TypeMatches(CodeTypeEntry type, string wanted) =>
        string.Equals(type.Name, wanted, StringComparison.OrdinalIgnoreCase)
        || string.Equals(type.FullName, wanted, StringComparison.OrdinalIgnoreCase);

    private static List<string> SplitList(string? value, string parameter, int max)
    {
        var items = (value ?? string.Empty)
            .Split(ListSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (items.Count == 0)
        {
            throw new ArgumentException($"{parameter} is required.");
        }

        return items.Count <= max
            ? items
            : throw new ArgumentException($"{parameter} accepts at most {max} comma-separated entries per call.");
    }

    private async Task<(CodeFileIndex File, bool Truncated)> ParseAsync(Workspace workspace, string? relativePath, CancellationToken ct)
    {
        var target = guard.Resolve(workspace, relativePath);
        if (!target.RelativePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"'{target.RelativePath}' is not a C# file. Use code_read for other files.");
        }

        if (!File.Exists(target.AbsolutePath))
        {
            throw new ArgumentException($"File not found: {target.RelativePath}. Use code_skeleton or code_folder_files to find it.");
        }

        var result = await indexer.IndexFileAsync(target.AbsolutePath, target.RelativePath, null, ct).ConfigureAwait(false);
        return result.Outcome switch
        {
            SyntaxIndexOutcome.Indexed => (result.File!, result.Truncated),
            SyntaxIndexOutcome.TooLarge => throw new ArgumentException($"'{target.RelativePath}' is too large to analyze. Read a range of it with code_read."),
            SyntaxIndexOutcome.NotText => throw new ArgumentException($"'{target.RelativePath}' looks binary and cannot be analyzed."),
            _ => throw new InvalidOperationException("The indexer returned an unexpected outcome."),
        };
    }

    // The parsed line numbers are only valid for the exact bytes that were parsed, so a file edited in between is refused.
    private async Task<LoadedText> LoadMatchingAsync(Workspace workspace, CodeFileIndex file, CancellationToken ct)
    {
        var target = guard.Resolve(workspace, file.Stamp.RelativePath);
        var source = await LoadAsync(target, ct).ConfigureAwait(false);
        return string.Equals(source.Hash, file.Stamp.Hash, StringComparison.Ordinal)
            ? source
            : throw new InvalidOperationException($"'{file.Stamp.RelativePath}' changed while it was being read. Try again.");
    }

    private async Task<LoadedText> LoadAsync(GuardedPath target, CancellationToken ct)
    {
        if (!File.Exists(target.AbsolutePath))
        {
            throw new ArgumentException($"File not found: {target.RelativePath}. Use code_skeleton or code_folder_files to find it.");
        }

        try
        {
            return await LoadedText.LoadAsync(target.AbsolutePath, options.MaxReadBytes, ct).ConfigureAwait(false);
        }
        catch (InvalidDataException ex)
        {
            throw new ArgumentException($"'{target.RelativePath}': {ex.Message}");
        }
    }
}
