using Waymark.Core.Code;
using Waymark.Core.Tools;

namespace Waymark.Modules.Code.Reading;

/// <summary>
/// Renders the source of selected members with the file's own line numbers. A member whose body would not fit in what is left of the answer is not
/// cut off half way: it is shown as a map instead (its local members with their line ranges, and its opening lines), so the reader can fetch the part it needs.
/// </summary>
internal static class MethodRenderer
{
    /// <summary>Room kept for the headers, the notes and the end of the answer when deciding whether a body fits.</summary>
    private const int ReserveChars = 1_500;

    /// <summary>Characters added to each source line by the line number prefix and the line break.</summary>
    private const int LinePrefixChars = 8;

    /// <summary>Most opening lines shown for a member too large to show whole.</summary>
    private const int MaxPreviewLines = 80;

    public static void Render(
        OutputBudget output,
        string workspace,
        CodeFileIndex file,
        LoadedText source,
        IReadOnlyList<CodeMemberEntry> found,
        IReadOnlyList<string> notFound,
        IReadOnlyList<string> available,
        int maxChars)
    {
        var path = file.Stamp.RelativePath;
        var tree = new OutlineRenderer.LocalTree(file);
        output.TryAppendLine(found.Count == 1
            ? $"# Member: `{found[0].Name}`"
            : $"# Members: {found.Count} found");
        output.TryAppendLine();
        output.TryAppendLine($"**Workspace:** {workspace}  ");
        output.TryAppendLine($"**File:** `{path}`");
        output.TryAppendLine();

        foreach (var member in found)
        {
            AppendMember(output, file, tree, source, member, maxChars);
        }

        if (notFound.Count == 0)
        {
            return;
        }

        output.TryAppendLine("---");
        output.TryAppendLine();
        output.TryAppendLine($"**Not found:** {string.Join(", ", notFound.Select(n => $"`{n}`"))}");
        if (available.Count > 0)
        {
            output.TryAppendLine($"**Members in this file:** {string.Join(", ", available.Select(n => $"`{n}`"))}");
        }
    }

    private static void AppendMember(OutputBudget output, CodeFileIndex file, OutlineRenderer.LocalTree tree, LoadedText source, CodeMemberEntry member, int maxChars)
    {
        var owner = member.TypeIndex is { } index ? file.Types[index] : null;
        var declaredIn = tree.OwnerChain(member);
        var title = owner is not null
            ? owner.Name + "." + member.Name
            : declaredIn.Count > 0 ? string.Join(" › ", declaredIn) + " › " + member.Name : member.Name;
        output.TryAppendLine($"## `{title}`");
        output.TryAppendLine();
        output.TryAppendLine("| Property | Value |");
        output.TryAppendLine("|----------|-------|");
        output.TryAppendLine($"| **Kind** | {member.Kind} |");
        if (owner is not null)
        {
            output.TryAppendLine($"| **Class** | `{owner.Name}` |");
            output.TryAppendLine($"| **{CodeLanguages.ContainerWord(file.Language)}** | `{owner.Namespace ?? "(none)"}` |");
        }

        if (declaredIn.Count > 0)
        {
            output.TryAppendLine($"| **Declared in** | `{declaredIn[^1]}` (a local member: it exists only inside that function's body) |");
        }

        output.TryAppendLine($"| **Lines** | {member.StartLine}-{member.EndLine} |");
        if (!string.IsNullOrEmpty(member.ReturnType))
        {
            output.TryAppendLine($"| **Return Type** | `{member.ReturnType}` |");
        }

        output.TryAppendLine($"| **Modifiers** | `{member.Modifiers ?? string.Empty}` |");
        output.TryAppendLine();
        if (!string.IsNullOrEmpty(member.DocSummary))
        {
            output.TryAppendLine($"_{member.DocSummary}_");
            output.TryAppendLine();
        }

        var last = Math.Min(member.EndLine, source.LineCount);
        if (output.Length + BodyChars(source, member.StartLine, last) + ReserveChars > maxChars)
        {
            AppendMap(output, file, tree, source, member, last, maxChars);
            return;
        }

        output.TryAppendLine($"```{CodeLanguages.Fence(file.Language)}");
        for (var number = member.StartLine; number <= last; number++)
        {
            output.TryAppendLine($"{number,4} | {source.Line(number)}");
        }

        output.TryAppendLine("```");
        output.TryAppendLine();
    }

    /// <summary>
    /// A member too large to show whole: its size, its local members (nested ones indented) with their line ranges, and its opening lines up to the first
    /// local member, which for a component are the props, state and refs. The rest is one code_method or code_read call away.
    /// </summary>
    private static void AppendMap(OutputBudget output, CodeFileIndex file, OutlineRenderer.LocalTree tree, LoadedText source, CodeMemberEntry member, int last, int maxChars)
    {
        var lineCount = last - member.StartLine + 1;
        var chars = BodyChars(source, member.StartLine, last);
        var locals = tree.ChildrenOf(member).ToList();
        output.TryAppendLine(
            $"_Too large to show whole here: {lineCount} lines, about {chars:N0} characters. " +
            (locals.Count > 0
                ? $"Its {locals.Count} local member(s) are listed below; fetch them with code_method (several at once, comma separated; className='{member.Name}' when the name repeats in the file), "
                : string.Empty) +
            "or read a range with code_read(startLine, endLine)._");
        output.TryAppendLine();

        if (locals.Count > 0)
        {
            output.TryAppendLine($"### Local members of `{member.Name}`");
            output.TryAppendLine();
            output.TryAppendLine($"```{CodeLanguages.Fence(file.Language)}");
            AppendLocalLines(output, tree, member, 0);
            output.TryAppendLine("```");
            output.TryAppendLine();
        }

        // The opening lines: up to the first local member, never more than the preview limit or what still fits.
        var previewEnd = Math.Min(last, member.StartLine + MaxPreviewLines - 1);
        if (locals.Count > 0 && locals[0].StartLine > member.StartLine)
        {
            previewEnd = Math.Min(previewEnd, locals[0].StartLine - 1);
        }

        while (previewEnd > member.StartLine && output.Length + BodyChars(source, member.StartLine, previewEnd) + ReserveChars > maxChars)
        {
            previewEnd -= Math.Max(1, (previewEnd - member.StartLine) / 4);
        }

        output.TryAppendLine($"### Opening lines {member.StartLine}-{previewEnd}");
        output.TryAppendLine();
        output.TryAppendLine($"```{CodeLanguages.Fence(file.Language)}");
        for (var number = member.StartLine; number <= previewEnd; number++)
        {
            output.TryAppendLine($"{number,4} | {source.Line(number)}");
        }

        output.TryAppendLine("```");
        output.TryAppendLine();
        if (previewEnd < last)
        {
            output.TryAppendLine($"_Lines {previewEnd + 1}-{last} not shown._");
            output.TryAppendLine();
        }
    }

    private static void AppendLocalLines(OutputBudget output, OutlineRenderer.LocalTree tree, CodeMemberEntry owner, int depth)
    {
        if (depth > OutlineRenderer.LocalTree.MaxRenderDepth)
        {
            return;
        }

        foreach (var child in tree.ChildrenOf(owner))
        {
            output.TryAppendLine($"{new string(' ', depth * 2)}{child.Name}  {OutlineRenderer.LineRange(child.StartLine, child.EndLine)}  {child.Signature}");
            AppendLocalLines(output, tree, child, depth + 1);
        }
    }

    private static long BodyChars(LoadedText source, int first, int last)
    {
        long total = 0;
        for (var number = first; number <= last; number++)
        {
            total += source.Line(number).Length + LinePrefixChars;
        }

        return total;
    }
}
