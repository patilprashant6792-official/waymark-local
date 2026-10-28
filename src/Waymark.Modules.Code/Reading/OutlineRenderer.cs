using Waymark.Core.Code;
using Waymark.Core.Tools;

namespace Waymark.Modules.Code.Reading;

/// <summary>Renders the structure of one parsed source file as markdown: types, then their members with line ranges, then the functions and variables outside any type.</summary>
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

    private static readonly CodeMemberKind[] ModuleOrder = [CodeMemberKind.Function, CodeMemberKind.Variable];

    public static string Heading(CodeMemberKind kind) => kind switch
    {
        CodeMemberKind.EnumMember => "Enum members",
        CodeMemberKind.Constructor => "Constructors",
        CodeMemberKind.Property => "Properties",
        CodeMemberKind.Indexer => "Indexers",
        CodeMemberKind.Field => "Fields",
        CodeMemberKind.Event => "Events",
        CodeMemberKind.Operator => "Operators",
        CodeMemberKind.Function => "Functions",
        CodeMemberKind.Variable => "Variables",
        _ => "Methods",
    };

    public static bool IsPublic(string? modifiers) =>
        modifiers is not null && modifiers.Split(' ').Contains("public", StringComparer.Ordinal);

    public static string LineRange(int start, int end, string comment = "//") =>
        start == end ? $"{comment} Line {start}" : $"{comment} Lines {start}-{end}";

    public static void Render(OutputBudget output, string workspace, CodeFileIndex file, bool includePrivate, bool truncated)
    {
        var path = file.Stamp.RelativePath;
        var language = file.Language;
        output.TryAppendLine($"# {CodeLanguages.DisplayName(language)} File Analysis: `{Path.GetFileName(path)}`");
        output.TryAppendLine();
        output.TryAppendLine($"**Workspace:** {workspace}  ");
        output.TryAppendLine($"**Path:** `{path}`  ");
        output.TryAppendLine($"**Lines:** {file.LineCount} | **Classification:** {file.Classification}  ");
        var namespaces = file.Types.Select(t => t.Namespace).Where(n => !string.IsNullOrEmpty(n)).Distinct(StringComparer.Ordinal).ToList();
        if (namespaces.Count == 0 && language == CodeLanguage.Python && Waymark.Languages.Python.PythonModuleNames.FromPath(path) is { Length: > 0 } module)
        {
            // A module with only functions or variables has no type to carry its name.
            namespaces.Add(module);
        }

        output.TryAppendLine($"**{CodeLanguages.ContainerWord(language)}:** {(namespaces.Count == 0 ? "(none)" : string.Join(", ", namespaces.Select(n => $"`{n}`")))}");
        output.TryAppendLine(includePrivate
            ? "**Members shown:** all"
            : "**Members shown:** public only (set includePrivateMembers=true for all)");
        if (truncated)
        {
            output.TryAppendLine("_The file hit the indexing limits; the last members are not listed._");
        }

        output.TryAppendLine();
        AppendUsings(output, file);
        var moduleMembers = ModuleMembers(file, includePrivate);
        if (file.Types.Count == 0 && moduleMembers.Count == 0)
        {
            output.TryAppendLine(language == CodeLanguage.CSharp ? "*No types found*" : "*No classes, functions or variables found*");
            return;
        }

        if (file.Types.Count > 0)
        {
            output.TryAppendLine($"## Types ({file.Types.Count})");
            output.TryAppendLine();
            for (var i = 0; i < file.Types.Count; i++)
            {
                AppendType(output, file, i, includePrivate);
            }
        }

        AppendModuleMembers(output, file, moduleMembers);
    }

    private static List<CodeMemberEntry> ModuleMembers(CodeFileIndex file, bool includePrivate) =>
        [.. file.Members.Where(m => m.TypeIndex is null && (includePrivate || IsPublic(m.Modifiers))).OrderBy(m => m.StartLine)];

    private static void AppendUsings(OutputBudget output, CodeFileIndex file)
    {
        if (file.Usings.Count == 0)
        {
            return;
        }

        var csharp = file.Language == CodeLanguage.CSharp;
        output.TryAppendLine("<details>");
        output.TryAppendLine($"<summary>{CodeLanguages.ImportsTitle(file.Language)}</summary>");
        output.TryAppendLine();
        output.TryAppendLine($"```{CodeLanguages.Fence(file.Language)}");
        foreach (var item in file.Usings)
        {
            output.TryAppendLine(csharp ? $"using {item};" : item);
        }

        output.TryAppendLine("```");
        output.TryAppendLine("</details>");
        output.TryAppendLine();
    }

    private static void AppendType(OutputBudget output, CodeFileIndex file, int index, bool includePrivate)
    {
        var type = file.Types[index];
        var language = file.Language;
        var comment = CommentMarker(language);
        output.TryAppendLine($"### `{type.Name}` ({LineRange(type.StartLine, type.EndLine, comment).TrimStart('/', '#', ' ')})");
        output.TryAppendLine();
        if (!string.IsNullOrEmpty(type.DocSummary))
        {
            output.TryAppendLine($"_{type.DocSummary}_");
            output.TryAppendLine();
        }

        output.TryAppendLine($"```{CodeLanguages.Fence(language)}");
        AppendAttributes(output, type.Attributes, language);
        output.TryAppendLine(TypeHeader(type, language));
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
            AppendGroup(output, language, kind, members.Where(m => m.Kind == kind).ToList(), "####");
        }
    }

    private static void AppendModuleMembers(OutputBudget output, CodeFileIndex file, List<CodeMemberEntry> members)
    {
        foreach (var kind in ModuleOrder)
        {
            AppendGroup(output, file.Language, kind, members.Where(m => m.Kind == kind).ToList(), "##");
        }
    }

    private static void AppendGroup(OutputBudget output, CodeLanguage language, CodeMemberKind kind, List<CodeMemberEntry> group, string heading)
    {
        if (group.Count == 0)
        {
            return;
        }

        var comment = CommentMarker(language);
        var csharp = language == CodeLanguage.CSharp;
        output.TryAppendLine($"{heading} {Heading(kind)} ({group.Count})");
        output.TryAppendLine();
        output.TryAppendLine($"```{CodeLanguages.Fence(language)}");
        foreach (var member in group)
        {
            AppendAttributes(output, member.Attributes, language);

            // C# shows the modifiers as written; for Python they only say public or private, which the listing already reflects.
            var memberModifiers = csharp && !string.IsNullOrEmpty(member.Modifiers) ? member.Modifiers + " " : string.Empty;
            output.TryAppendLine($"{memberModifiers}{member.Signature} {LineRange(member.StartLine, member.EndLine, comment)}");
        }

        output.TryAppendLine("```");
        output.TryAppendLine();
    }

    private static string TypeHeader(CodeTypeEntry type, CodeLanguage language)
    {
        if (language == CodeLanguage.CSharp)
        {
            var modifiers = string.IsNullOrEmpty(type.Modifiers) ? string.Empty : type.Modifiers + " ";
            var bases = string.IsNullOrEmpty(type.BaseList) ? string.Empty : " : " + type.BaseList;
            return $"{modifiers}{KindWord(type.Kind)} {type.Name}{bases}";
        }

        if (type.Kind == CodeTypeKind.TypeAlias)
        {
            return $"type {type.Name}  # type alias";
        }

        var parents = string.IsNullOrEmpty(type.BaseList) ? string.Empty : $"({type.BaseList})";
        var note = type.Kind == CodeTypeKind.Class ? string.Empty : $"  # {KindWord(type.Kind)}";
        return $"class {type.Name}{parents}{note}";
    }

    private static string CommentMarker(CodeLanguage language) => language == CodeLanguage.CSharp ? "//" : "#";

    private static void AppendAttributes(OutputBudget output, string? attributes, CodeLanguage language)
    {
        if (string.IsNullOrEmpty(attributes))
        {
            return;
        }

        foreach (var attribute in attributes.Split('\n'))
        {
            output.TryAppendLine(CodeLanguages.FormatAttribute(language, attribute));
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
        CodeTypeKind.TypeAlias => "type alias",
        _ => "class",
    };
}
