using Waymark.Core.Code;
using Waymark.Core.Tools;

namespace Waymark.Modules.Code.Reading;

/// <summary>Renders the source of selected members with the file's own line numbers.</summary>
internal static class MethodRenderer
{
    public static void Render(
        OutputBudget output,
        string workspace,
        CodeFileIndex file,
        LoadedText source,
        IReadOnlyList<CodeMemberEntry> found,
        IReadOnlyList<string> notFound,
        IReadOnlyList<string> available)
    {
        var path = file.Stamp.RelativePath;
        output.TryAppendLine(found.Count == 1
            ? $"# Member: `{found[0].Name}`"
            : $"# Members: {found.Count} found");
        output.TryAppendLine();
        output.TryAppendLine($"**Workspace:** {workspace}  ");
        output.TryAppendLine($"**File:** `{path}`");
        output.TryAppendLine();

        foreach (var member in found)
        {
            AppendMember(output, file, source, member);
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

    private static void AppendMember(OutputBudget output, CodeFileIndex file, LoadedText source, CodeMemberEntry member)
    {
        var owner = member.TypeIndex is { } index ? file.Types[index] : null;
        output.TryAppendLine($"## `{(owner is null ? member.Name : owner.Name + "." + member.Name)}`");
        output.TryAppendLine();
        output.TryAppendLine("| Property | Value |");
        output.TryAppendLine("|----------|-------|");
        output.TryAppendLine($"| **Kind** | {member.Kind} |");
        if (owner is not null)
        {
            output.TryAppendLine($"| **Class** | `{owner.Name}` |");
            output.TryAppendLine($"| **Namespace** | `{owner.Namespace ?? "(none)"}` |");
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

        output.TryAppendLine("```csharp");
        var last = Math.Min(member.EndLine, source.LineCount);
        for (var number = member.StartLine; number <= last; number++)
        {
            output.TryAppendLine($"{number,4} | {source.Line(number)}");
        }

        output.TryAppendLine("```");
        output.TryAppendLine();
    }
}
