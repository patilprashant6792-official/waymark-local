using Waymark.Core.Code;
using Waymark.Core.Memory;
using Waymark.Core.Security;
using Waymark.Core.Workspaces;
using Waymark.Modules.Code.Reading;

namespace Waymark.Modules.Code.Memory;

/// <summary>A file read once for a call: its text, its parsed index (code files only), or why it is not available.</summary>
internal sealed record FileView(string Path, LoadedText? Text, CodeFileIndex? Index, string? Problem);

/// <summary>What a step is in the code: a method (or any other member), a type, a whole file or a range of lines.</summary>
internal enum StepKind
{
    Method,
    Type,
    File,
    Lines,
}

/// <summary>
/// One step as the code on disk shows it: whether it is there, where, and its signature. Never its body: the agent reads the bodies it needs with code_method.
/// <paramref name="Owner"/> and <paramref name="Member"/> are the declaring type and the member of a method step; <paramref name="File"/> is the parsed file, kept for the link check.
/// </summary>
internal sealed record StepView(
    FeatureComponent Component,
    bool Found,
    string? Problem,
    StepKind Kind,
    string? Signature,
    int StartLine,
    int EndLine,
    int Overloads,
    CodeTypeEntry? Owner,
    CodeMemberEntry? Member,
    FileView? File);

/// <summary>
/// Looks up the steps of a flow in the code that is on disk now, through the path guard and with the parsers of code_outline and code_method.
/// A step that cannot be found is reported with the reason; nothing is guessed or repaired. It describes a step and never returns its code.
/// </summary>
internal sealed class FlowStepReader(IPathGuard guard, LanguageIndexers languages, CodeReadOptions options)
{
    private const int MaxAvailableNames = 20;
    private const string NotAnalyzable = "is not a source file the code tools can analyze";

    /// <param name="files">The files read so far in this call, so a file shared by several steps is read and parsed once.</param>
    public async Task<StepView> DescribeAsync(Workspace workspace, FlowStep step, Dictionary<string, FileView> files, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(files);
        var component = step.Component;
        if (!step.Valid)
        {
            return Missing(component, "the saved text is not a valid component", null);
        }

        if (!files.TryGetValue(component.Path, out var file))
        {
            file = await LoadAsync(workspace, component.Path, ct).ConfigureAwait(false);
            files[component.Path] = file;
        }

        var text = file.Text;
        if (text is null)
        {
            return Missing(component, file.Problem ?? "cannot be read", file);
        }

        if (component.IsRange)
        {
            var start = component.StartLine ?? 1;
            if (start > text.LineCount)
            {
                return Missing(component, $"the file has only {text.LineCount} lines", file);
            }

            var end = Math.Min(component.EndLine ?? start, text.LineCount);
            return new StepView(component, true, null, StepKind.Lines, null, start, end, 0, null, null, file);
        }

        if (component.IsFile)
        {
            var signature = file.Index is null ? null : $"{file.Index.Types.Count} types, {file.Index.Members.Count} members";
            return new StepView(component, true, null, StepKind.File, signature, 1, text.LineCount, 0, null, null, file);
        }

        return file.Index is null
            ? Missing(component, file.Problem ?? $"{NotAnalyzable}; save a line range of it instead", file)
            : Symbol(component, file.Index, file);
    }

    private static StepView Symbol(FeatureComponent component, CodeFileIndex index, FileView file)
    {
        var name = component.Member!;
        List<CodeMemberEntry> members;
        if (component.Type is null)
        {
            var named = TypesNamed(index, name);
            if (named.Count > 0)
            {
                return TypeView(component, index, named[0], file);
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
                    return TypeView(component, index, qualified[0], file);
                }
            }
        }

        if (members.Count == 0)
        {
            var names = index.Types.Select(t => t.Name).Concat(index.Members.Select(m => m.Name)).Distinct(StringComparer.Ordinal).Take(MaxAvailableNames).ToList();
            var have = names.Count == 0 ? "nothing it can name" : string.Join(", ", names);
            return Missing(component, $"no such type or member in the file (it has: {have})", file);
        }

        var first = members.OrderBy(m => m.StartLine).First();
        var owner = first.TypeIndex is { } typeIndex ? index.Types[typeIndex] : null;
        return new StepView(component, true, null, StepKind.Method, first.Signature, first.StartLine, first.EndLine, members.Count, owner, first, file);
    }

    private static StepView TypeView(FeatureComponent component, CodeFileIndex index, int typeIndex, FileView file)
    {
        var type = index.Types[typeIndex];
        var bases = string.IsNullOrWhiteSpace(type.BaseList) ? string.Empty : " : " + type.BaseList.TrimStart(':', ' ');
        var signature = $"{type.Kind.ToString().ToLowerInvariant()} {type.Name}{bases}";
        return new StepView(component, true, null, StepKind.Type, signature, type.StartLine, type.EndLine, 0, type, null, file);
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

    private static StepView Missing(FeatureComponent component, string problem, FileView? file) =>
        new(component, false, problem, KindOf(component), null, 0, 0, 0, null, null, file);

    private static StepKind KindOf(FeatureComponent component) =>
        component.IsRange ? StepKind.Lines : component.IsFile ? StepKind.File : StepKind.Method;

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
