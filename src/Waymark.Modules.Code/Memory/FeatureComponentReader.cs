using Waymark.Core.Code;
using Waymark.Core.Memory;
using Waymark.Core.Security;
using Waymark.Core.Tools;
using Waymark.Core.Workspaces;
using Waymark.Modules.Code.Reading;

namespace Waymark.Modules.Code.Memory;

/// <summary>A file read once for a call: its text, its parsed index (code files only), or why it is not available.</summary>
internal sealed record FileView(string Path, LoadedText? Text, CodeFileIndex? Index, string? Problem);

/// <summary>One component as the code on disk shows it. Body is its text; Problem says why it is not available; TooLong marks one that exists but does not fit one answer.</summary>
internal sealed record ComponentView(FeatureComponent Component, string? Body, string? Problem, bool TooLong);

/// <summary>
/// Looks up saved components in the code that is on disk now, through the path guard and with the parsers and renderers of code_outline, code_method and code_read.
/// A component that cannot be found is reported with the reason; nothing is guessed or repaired.
/// </summary>
internal sealed class FeatureComponentReader(IPathGuard guard, LanguageIndexers languages, CodeReadOptions options, ToolPipelineOptions pipeline)
{
    private const int MaxAvailableNames = 20;
    private const string NotAnalyzable = "is not a source file the code tools can analyze";

    /// <param name="files">The files read so far in this call, so a file shared by several components is read and parsed once.</param>
    public async Task<ComponentView> ReadAsync(Workspace workspace, FeatureComponent component, Dictionary<string, FileView> files, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(component);
        ArgumentNullException.ThrowIfNull(files);
        if (!files.TryGetValue(component.Path, out var file))
        {
            file = await LoadAsync(workspace, component.Path, ct).ConfigureAwait(false);
            files[component.Path] = file;
        }

        var text = file.Text;
        if (text is null)
        {
            return Missing(component, file.Problem ?? "cannot be read");
        }

        var output = new OutputBudget(pipeline.MaxOutputChars);
        if (component.IsRange)
        {
            var start = component.StartLine ?? 1;
            if (start > text.LineCount)
            {
                return Missing(component, $"the file has only {text.LineCount} lines");
            }

            LineReader.AppendRange(output, text, file.Path, start, component.EndLine ?? start);
            return Present(component, output);
        }

        if (component.IsFile)
        {
            if (file.Index is null)
            {
                LineReader.AppendAll(output, text, file.Path);
            }
            else
            {
                OutlineRenderer.Render(output, workspace.Name, file.Index, false, false);
            }

            return Present(component, output);
        }

        return file.Index is null
            ? Missing(component, file.Problem ?? $"{NotAnalyzable}; save a line range of it instead")
            : Symbol(component, file.Index, text, output);
    }

    // 'path:A.B' is member B of type A (or, failing that, the type 'A.B'). 'path:Name' is a type called Name, otherwise a member called Name.
    private static ComponentView Symbol(FeatureComponent component, CodeFileIndex index, LoadedText text, OutputBudget output)
    {
        var name = component.Member!;
        List<CodeMemberEntry> members;
        if (component.Type is null)
        {
            var named = TypesNamed(index, name);
            if (named.Count > 0)
            {
                return Types(component, index, named, output);
            }

            members = MembersNamed(index, null, name);
        }
        else
        {
            members = MembersNamed(index, component.Type, name);
            if (members.Count == 0)
            {
                var qualified = TypesNamed(index, component.Type + "." + name);
                if (qualified.Count > 0)
                {
                    return Types(component, index, qualified, output);
                }
            }
        }

        if (members.Count == 0)
        {
            var names = index.Types.Select(t => t.Name).Concat(index.Members.Select(m => m.Name)).Distinct(StringComparer.Ordinal).Take(MaxAvailableNames).ToList();
            var have = names.Count == 0 ? "nothing it can name" : string.Join(", ", names);
            return Missing(component, $"no such type or member in the file (it has: {have})");
        }

        foreach (var member in members)
        {
            AppendMember(output, index, text, member);
        }

        return Present(component, output);
    }

    private static ComponentView Types(FeatureComponent component, CodeFileIndex index, List<int> typeIndexes, OutputBudget output)
    {
        foreach (var typeIndex in typeIndexes)
        {
            AppendType(output, index, typeIndex);
        }

        return Present(component, output);
    }

    private static void AppendType(OutputBudget output, CodeFileIndex index, int typeIndex)
    {
        var type = index.Types[typeIndex];
        var comment = index.Language == CodeLanguage.Python ? "#" : "//";
        output.TryAppendLine($"#### {type.Name} (lines {type.StartLine}-{type.EndLine})");
        if (!string.IsNullOrEmpty(type.DocSummary))
        {
            output.TryAppendLine($"_{type.DocSummary}_");
        }

        output.TryAppendLine($"```{CodeLanguages.Fence(index.Language)}");
        foreach (var member in index.Members.Where(m => m.TypeIndex == typeIndex).OrderBy(m => m.StartLine))
        {
            output.TryAppendLine($"{member.Signature}  {comment} {member.StartLine}-{member.EndLine}");
        }

        output.TryAppendLine("```");
    }

    private static void AppendMember(OutputBudget output, CodeFileIndex index, LoadedText text, CodeMemberEntry member)
    {
        var owner = member.TypeIndex is { } typeIndex ? index.Types[typeIndex].Name + "." : string.Empty;
        var last = Math.Min(member.EndLine, text.LineCount);
        output.TryAppendLine($"#### {owner}{member.Name} (lines {member.StartLine}-{last})");
        output.TryAppendLine($"```{CodeLanguages.Fence(index.Language)}");
        for (var number = member.StartLine; number <= last; number++)
        {
            output.TryAppendLine($"{number,4} | {text.Line(number)}");
        }

        output.TryAppendLine("```");
    }

    private static List<int> TypesNamed(CodeFileIndex index, string wanted) =>
        Enumerable.Range(0, index.Types.Count).Where(i => TypeMatches(index.Types[i], wanted)).ToList();

    private static List<CodeMemberEntry> MembersNamed(CodeFileIndex index, string? type, string name) =>
        index.Members
            .Where(member =>
                (string.Equals(member.Name, name, StringComparison.OrdinalIgnoreCase) || IsConstructorAlias(member, name))
                && (type is null || (member.TypeIndex is { } owner && TypeMatches(index.Types[owner], type))))
            .ToList();

    private static bool TypeMatches(CodeTypeEntry type, string wanted) =>
        string.Equals(type.Name, wanted, StringComparison.OrdinalIgnoreCase)
        || string.Equals(type.FullName, wanted, StringComparison.OrdinalIgnoreCase)
        || type.FullName.EndsWith("." + wanted, StringComparison.OrdinalIgnoreCase);

    // A Python class is created through __init__, so that name reaches the constructor, which the index names after its class.
    private static bool IsConstructorAlias(CodeMemberEntry member, string name) =>
        member.Kind == CodeMemberKind.Constructor && string.Equals(name, "__init__", StringComparison.Ordinal);

    private static ComponentView Present(FeatureComponent component, OutputBudget output) =>
        output.IsTruncated
            ? new ComponentView(component, null, null, true)
            : new ComponentView(component, output.Build().TrimEnd(), null, false);

    private static ComponentView Missing(FeatureComponent component, string problem) => new(component, null, problem, false);

    private async Task<FileView> LoadAsync(Workspace workspace, string path, CancellationToken ct)
    {
        GuardedPath target;
        try
        {
            target = guard.Resolve(workspace, path);
        }
        catch (Exception ex) when (ex is FileAccessDeniedException or ArgumentException)
        {
            return new FileView(path, null, null, "is not readable here (outside the workspace, or blocked by the access rules)");
        }

        if (!File.Exists(target.AbsolutePath))
        {
            return new FileView(target.RelativePath, null, null, "file not found");
        }

        LoadedText text;
        try
        {
            text = await LoadedText.LoadAsync(target.AbsolutePath, options.MaxReadBytes, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return new FileView(target.RelativePath, null, null, ex is InvalidDataException ? ex.Message : "could not be read");
        }

        if (!languages.TryGet(target.RelativePath, out var indexer))
        {
            return new FileView(target.RelativePath, text, null, null);
        }

        try
        {
            var result = await indexer.IndexFileAsync(target.AbsolutePath, target.RelativePath, null, ct).ConfigureAwait(false);
            if (result.Outcome != SyntaxIndexOutcome.Indexed || result.File is null)
            {
                return new FileView(target.RelativePath, text, null, "cannot be analyzed (too large, binary, or its parser did not load)");
            }

            // The parsed line numbers hold only for the bytes that were parsed; a file edited in between is not trusted.
            return string.Equals(result.File.Stamp.Hash, text.Hash, StringComparison.Ordinal)
                ? new FileView(target.RelativePath, text, result.File, null)
                : new FileView(target.RelativePath, text, null, "changed while it was being read; try again");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new FileView(target.RelativePath, text, null, "could not be analyzed");
        }
    }
}
