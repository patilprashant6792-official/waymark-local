using Waymark.Core.Code;
using Waymark.Core.Tools;

namespace Waymark.Modules.Code.Reading;

/// <summary>Renders the structure of one parsed C# file as markdown: types, then their members with line ranges.</summary>
internal static class OutlineRenderer
{
    private static readonly CodeMemberKind[] MemberOrder =
    [
        CodeMemberKind.EnumMember,
        CodeMemberKind.Constructor,
        CodeMemberKind.Property,
        CodeMemberKind.Indexer,
        CodeMemberKind.Field,
        CodeMemberKind.Event,
        CodeMemberKind.Operator,
        CodeMemberKind.Method,
    ];

    public static string Heading(CodeMemberKind kind) => kind switch
    {
        CodeMemberKind.EnumMember => "Enum members",
        CodeMemberKind.Constructor => "Constructors",
        CodeMemberKind.Property => "Properties",
        CodeMemberKind.Indexer => "Indexers",
        CodeMemberKind.Field => "Fields",
        CodeMemberKind.Event => "Events",
        CodeMemberKind.Operator => "Operators",
        _ => "Methods",
    };

    public static bool IsPublic(string? modifiers) =>
        modifiers is not null && modifiers.Split(' ').Contains("public", StringComparer.Ordinal);

    public static string LineRange(int start, int end) =>
        start == end ? $"// Line {start}" : $"// Lines {start}-{end}";

    public static void Render(OutputBudget output, string workspace, CodeFileIndex file, bool includePrivate, bool truncated)
    {
        var path = file.Stamp.RelativePath;
        output.TryAppendLine($"# C# File Analysis: `{Path.GetFileName(path)}`");
        output.TryAppendLine();
        output.TryAppendLine($"**Workspace:** {workspace}  ");
        output.TryAppendLine($"**Path:** `{path}`  ");
        output.TryAppendLine($"**Lines:** {file.LineCount} | **Classification:** {file.Classification}  ");
        var namespaces = file.Types.Select(t => t.Namespace).Where(n => !string.IsNullOrEmpty(n)).Distinct(StringComparer.Ordinal).ToList();
        output.TryAppendLine($"**Namespace:** {(namespaces.Count == 0 ? "(none)" : string.Join(", ", namespaces.Select(n => $"`{n}`")))}");
        output.TryAppendLine(includePrivate
            ? "**Members shown:** all"
            : "**Members shown:** public only (set includePrivateMembers=true for all)");
        if (truncated)
        {
            output.TryAppendLine("_The file hit the indexing limits; the last members are not listed._");
        }

        output.TryAppendLine();
        AppendUsings(output, file.Usings);
        if (file.Types.Count == 0)
        {
            output.TryAppendLine("*No types found*");
            return;
        }

        output.TryAppendLine($"## Types ({file.Types.Count})");
        output.TryAppendLine();
        for (var i = 0; i < file.Types.Count; i++)
        {
            AppendType(output, file, i, includePrivate);
        }
    }

    private static void AppendUsings(OutputBudget output, IReadOnlyList<string> usings)
    {
        if (usings.Count == 0)
        {
            return;
        }

        output.TryAppendLine("<details>");
        output.TryAppendLine("<summary>Using Directives</summary>");
        output.TryAppendLine();
        output.TryAppendLine("```csharp");
        foreach (var item in usings)
        {
            output.TryAppendLine($"using {item};");
        }

        output.TryAppendLine("```");
        output.TryAppendLine("</details>");
        output.TryAppendLine();
    }

    private static void AppendType(OutputBudget output, CodeFileIndex file, int index, bool includePrivate)
    {
        var type = file.Types[index];
        output.TryAppendLine($"### `{type.Name}` ({LineRange(type.StartLine, type.EndLine).TrimStart('/', ' ')})");
        output.TryAppendLine();
        if (!string.IsNullOrEmpty(type.DocSummary))
        {
            output.TryAppendLine($"_{type.DocSummary}_");
            output.TryAppendLine();
        }

        output.TryAppendLine("```csharp");
        AppendAttributes(output, type.Attributes);
        var modifiers = string.IsNullOrEmpty(type.Modifiers) ? string.Empty : type.Modifiers + " ";
        var bases = string.IsNullOrEmpty(type.BaseList) ? string.Empty : " : " + type.BaseList;
        output.TryAppendLine($"{modifiers}{KindWord(type.Kind)} {type.Name}{bases}");
        output.TryAppendLine("```");
        output.TryAppendLine();

        var members = file.Members
            .Where(m => m.TypeIndex == index && (includePrivate || IsPublic(m.Modifiers)))
            .OrderBy(m => m.StartLine)
            .ToList();
        if (members.Count == 0)
        {
            output.TryAppendLine(includePrivate ? "*No members*" : "*No public members*");
            output.TryAppendLine();
            return;
        }

        foreach (var kind in MemberOrder)
        {
            var group = members.Where(m => m.Kind == kind).ToList();
            if (group.Count == 0)
            {
                continue;
            }

            output.TryAppendLine($"#### {Heading(kind)} ({group.Count})");
            output.TryAppendLine();
            output.TryAppendLine("```csharp");
            foreach (var member in group)
            {
                AppendAttributes(output, member.Attributes);
                var memberModifiers = string.IsNullOrEmpty(member.Modifiers) ? string.Empty : member.Modifiers + " ";
                output.TryAppendLine($"{memberModifiers}{member.Signature} {LineRange(member.StartLine, member.EndLine)}");
            }

            output.TryAppendLine("```");
            output.TryAppendLine();
        }
    }

    private static void AppendAttributes(OutputBudget output, string? attributes)
    {
        if (string.IsNullOrEmpty(attributes))
        {
            return;
        }

        foreach (var attribute in attributes.Split('\n'))
        {
            output.TryAppendLine($"[{attribute}]");
        }
    }

    private static string KindWord(CodeTypeKind kind) => kind switch
    {
        CodeTypeKind.Interface => "interface",
        CodeTypeKind.Struct => "struct",
        CodeTypeKind.Record => "record",
        CodeTypeKind.RecordStruct => "record struct",
        CodeTypeKind.Enum => "enum",
        CodeTypeKind.Delegate => "delegate",
        _ => "class",
    };
}
