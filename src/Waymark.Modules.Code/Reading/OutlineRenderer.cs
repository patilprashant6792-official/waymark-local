using Waymark.Core.Code;
using Waymark.Core.Tools;

namespace Waymark.Modules.Code.Reading;

/// <summary>
/// Renders the structure of one parsed source file as markdown: types, then their members with line ranges, then the functions and variables outside any type.
/// Local members (functions declared inside another function, such as the handlers of a React component) are listed indented under the function that declares them.
/// </summary>
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
        CodeMemberKind.Function,
        CodeMemberKind.Variable,
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
        if (namespaces.Count == 0)
        {
            // A module with only functions or variables has no type to carry its name.
            var module = language switch
            {
                CodeLanguage.Python => Waymark.Languages.Python.PythonModuleNames.FromPath(path),
                CodeLanguage.TypeScript or CodeLanguage.JavaScript => Waymark.Languages.TypeScript.TypeScriptModuleNames.FromPath(path),
                _ => string.Empty,
            };
            if (module.Length > 0)
            {
                namespaces.Add(module);
            }
        }

        output.TryAppendLine($"**{CodeLanguages.ContainerWord(language)}:** {(namespaces.Count == 0 ? "(none)" : string.Join(", ", namespaces.Select(n => $"`{n}`")))}");
        output.TryAppendLine(includePrivate
            ? "**Members shown:** all"
            : "**Members shown:** public only (set includePrivateMembers=true for all)");
        if (truncated)
        {
            output.TryAppendLine("_The file hit the indexing limits; the last members are not listed._");
        }

        var tree = new LocalTree(file);
        if (tree.LocalCount > 0)
        {
            output.TryAppendLine($"_{tree.LocalCount} local member(s) (handlers, callbacks, inner helpers) are indented under the function that declares them; fetch one by name with code_method._");
        }

        output.TryAppendLine();
        AppendUsings(output, file);
        var moduleMembers = ModuleMembers(file, tree, includePrivate);
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
                AppendType(output, file, tree, i, includePrivate);
            }
        }

        AppendModuleMembers(output, file, tree, moduleMembers);
    }

    // A local member that has an owner is shown under it, whatever its own visibility; the rest follow the visibility rule.
    private static List<CodeMemberEntry> ModuleMembers(CodeFileIndex file, LocalTree tree, bool includePrivate) =>
        [.. file.Members.Where(m => m.TypeIndex is null && !tree.HasOwner(m) && (includePrivate || IsPublic(m.Modifiers))).OrderBy(m => m.StartLine)];

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

    private static void AppendType(OutputBudget output, CodeFileIndex file, LocalTree tree, int index, bool includePrivate)
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
            AppendGroup(output, language, tree, kind, members.Where(m => m.Kind == kind).ToList(), "####");
        }
    }

    private static void AppendModuleMembers(OutputBudget output, CodeFileIndex file, LocalTree tree, List<CodeMemberEntry> members)
    {
        foreach (var kind in ModuleOrder)
        {
            AppendGroup(output, file.Language, tree, kind, members.Where(m => m.Kind == kind).ToList(), "##");
        }
    }

    private static void AppendGroup(OutputBudget output, CodeLanguage language, LocalTree tree, CodeMemberKind kind, List<CodeMemberEntry> group, string heading)
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
            var (exportPrefix, exportSuffix) = ExportMarker(language, member);
            output.TryAppendLine($"{memberModifiers}{exportPrefix}{member.Signature} {LineRange(member.StartLine, member.EndLine, comment)}{exportSuffix}");
            AppendLocals(output, tree, member, 1, comment);
        }

        output.TryAppendLine("```");
        output.TryAppendLine();
    }

    // A TypeScript or JavaScript module-level member that other files can import is marked, since the signature alone does not say so.
    // The marker goes in front only where the line stays valid code: `export default` cannot precede `const`, `let` or `var`,
    // so a default-exported constant (`const A = ...; export default A;`) gets a trailing "(export default)" instead.
    private static (string Prefix, string Suffix) ExportMarker(CodeLanguage language, CodeMemberEntry member)
    {
        if (!IsScript(language) || member.TypeIndex is not null || !IsPublic(member.Modifiers))
        {
            return (string.Empty, string.Empty);
        }

        if (!member.Modifiers!.Split(' ').Contains("default", StringComparer.Ordinal))
        {
            return ("export ", string.Empty);
        }

        return DeclaresVariable(member.Signature) ? (string.Empty, " (export default)") : ("export default ", string.Empty);
    }

    private static bool DeclaresVariable(string? signature) =>
        signature is not null
        && (signature.StartsWith("const ", StringComparison.Ordinal)
            || signature.StartsWith("let ", StringComparison.Ordinal)
            || signature.StartsWith("var ", StringComparison.Ordinal));

    /// <summary>The local members of <paramref name="owner"/>, two spaces deeper per level, each followed by its own locals.</summary>
    private static void AppendLocals(OutputBudget output, LocalTree tree, CodeMemberEntry owner, int depth, string comment)
    {
        if (depth > LocalTree.MaxRenderDepth)
        {
            return;
        }

        foreach (var child in tree.ChildrenOf(owner))
        {
            var value = child.Kind == CodeMemberKind.Variable ? " (value)" : string.Empty;
            output.TryAppendLine($"{new string(' ', depth * 2)}{child.Signature} {LineRange(child.StartLine, child.EndLine, comment)}{value}");
            AppendLocals(output, tree, child, depth + 1, comment);
        }
    }

    private static string TypeHeader(CodeTypeEntry type, CodeLanguage language)
    {
        if (language == CodeLanguage.CSharp)
        {
            var modifiers = string.IsNullOrEmpty(type.Modifiers) ? string.Empty : type.Modifiers + " ";
            var bases = string.IsNullOrEmpty(type.BaseList) ? string.Empty : " : " + type.BaseList;
            return $"{modifiers}{KindWord(type.Kind)} {type.Name}{bases}";
        }

        if (IsScript(language))
        {
            var exported = type.Modifiers is not null && type.Modifiers.Split(' ').Contains("public", StringComparer.Ordinal) ? "export " : string.Empty;
            var isAbstract = type.Modifiers is not null && type.Modifiers.Split(' ').Contains("abstract", StringComparer.Ordinal) ? "abstract " : string.Empty;
            var keyword = type.Kind == CodeTypeKind.TypeAlias ? "type" : KindWord(type.Kind);
            var extends = string.IsNullOrEmpty(type.BaseList)
                ? string.Empty
                : type.Kind == CodeTypeKind.Class ? $"  // extends / implements: {type.BaseList}" : $" extends {type.BaseList}";
            return $"{exported}{isAbstract}{keyword} {type.Name}{extends}";
        }

        if (type.Kind == CodeTypeKind.TypeAlias)
        {
            return $"type {type.Name}  # type alias";
        }

        var parents = string.IsNullOrEmpty(type.BaseList) ? string.Empty : $"({type.BaseList})";
        var note = type.Kind == CodeTypeKind.Class ? string.Empty : $"  # {KindWord(type.Kind)}";
        return $"class {type.Name}{parents}{note}";
    }

    private static string CommentMarker(CodeLanguage language) => language == CodeLanguage.Python ? "#" : "//";

    private static bool IsScript(CodeLanguage language) => language is CodeLanguage.TypeScript or CodeLanguage.JavaScript;

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
        CodeTypeKind.Namespace => "namespace",
        _ => "class",
    };

    /// <summary>Which local member belongs to which owner in one file, worked out once from the line ranges.</summary>
    internal sealed class LocalTree
    {
        /// <summary>A guard against a runaway nesting; the extractor itself stops well before.</summary>
        public const int MaxRenderDepth = 16;

        private readonly IReadOnlyList<CodeMemberEntry> _members;
        private readonly int[] _owners;
        private readonly Dictionary<int, List<int>> _children;
        private readonly Dictionary<CodeMemberEntry, int> _indexes = new(ReferenceEqualityComparer.Instance);

        public LocalTree(CodeFileIndex file)
        {
            ArgumentNullException.ThrowIfNull(file);
            _members = file.Members;
            _owners = LocalMembers.OwnerIndexes(_members);
            _children = LocalMembers.ChildrenByOwner(_members, _owners);
            for (var i = 0; i < _members.Count; i++)
            {
                _indexes[_members[i]] = i;
            }

            LocalCount = _owners.Count(o => o >= 0);
        }

        /// <summary>How many local members have an owner in this file.</summary>
        public int LocalCount { get; }

        public bool HasOwner(CodeMemberEntry member) => _indexes.TryGetValue(member, out var index) && _owners[index] >= 0;

        public IEnumerable<CodeMemberEntry> ChildrenOf(CodeMemberEntry member) =>
            _indexes.TryGetValue(member, out var index) && _children.TryGetValue(index, out var list)
                ? list.Select(i => _members[i])
                : [];

        /// <summary>The names of the members that declare <paramref name="member"/>, outermost first; empty when it is not local.</summary>
        public IReadOnlyList<string> OwnerChain(CodeMemberEntry member) =>
            _indexes.TryGetValue(member, out var index) ? LocalMembers.OwnerChain(_members, index, _owners) : [];
    }
}
