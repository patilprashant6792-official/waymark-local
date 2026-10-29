using System.Globalization;
using System.Text;
using Waymark.Core.Code;
using Waymark.Core.NuGetIndex;
using Waymark.Languages.TypeScript;
using Waymark.Modules.NuGet.Indexing;

namespace Waymark.Modules.NuGet.Npm;

/// <summary>
/// Turns the indexed declaration files of one npm package into the same entries the NuGet index holds: one per public class, interface, enum and type alias,
/// and one per method group, function or exported constant. Deterministic: the same files always give the same ids. A type alias is stored as a Struct (the
/// only kind TypeScript does not otherwise use). The module of a symbol is the path of its file inside the package (<c>lib/types</c>); the root
/// <c>index.d.ts</c> is named after the package. A <c>declare module "fs" { ... }</c> or <c>namespace N { ... }</c> block is a module of its own.
/// </summary>
internal sealed class NpmSymbolFactory(NuGetIndexOptions options)
{
    private const int MaxSymbols = 40_000;
    private const int MaxTokenNames = 80;
    private const int SummaryChars = 400;
    private const double EntryBonus = 25;

    private static readonly string[] ActionPrefixes =
    [
        "get", "create", "send", "add", "build", "run", "start", "open", "connect", "parse", "read", "write", "use", "set", "fetch", "load", "save", "render", "on",
    ];

    private sealed record KnownType(string Name, string Sid);

    /// <param name="files">One entry per file; the path in each is the path inside the package, which names its module.</param>
    /// <param name="entryModules">The paths of the types entry files (as <see cref="TypesEntryResolver"/> gives them).</param>
    public SymbolBuild Build(NuGetPackageKey key, string packageName, IReadOnlyList<CodeFileIndex> files, IReadOnlySet<string> entryModules)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(files);
        var entries = new HashSet<string>(entryModules.Select(e => TypeScriptModuleNames.FromPath(e)), StringComparer.OrdinalIgnoreCase);
        var modules = files
            .Select(file => (File: file, Module: ModuleOf(file.Stamp.RelativePath, packageName)))
            .Where(item => IsPublicModule(item.Module))
            .OrderBy(item => item.Module, StringComparer.Ordinal)
            .ThenBy(item => item.File.Stamp.RelativePath, StringComparer.Ordinal)
            .ToList();

        var known = new Dictionary<string, KnownType>(StringComparer.Ordinal);
        foreach (var (file, module) in modules)
        {
            foreach (var type in file.Types.Where(t => IsPublic(t.Modifiers) && t.Kind != CodeTypeKind.Namespace))
            {
                known.TryAdd(type.Name, new KnownType(type.Name, SymbolDocumentFactory.Sid(key, QualifiedName(module, type))));
            }
        }

        var symbols = new List<NuGetSymbolEntry>();
        var namespaces = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (file, module) in modules)
        {
            var isEntry = entries.Contains(TypeScriptModuleNames.FromPath(file.Stamp.RelativePath));
            for (var index = 0; index < file.Types.Count; index++)
            {
                var type = file.Types[index];
                if (!IsPublic(type.Modifiers))
                {
                    continue;
                }

                var members = file.Members.Where(m => m.TypeIndex == index).ToList();
                if (type.Kind == CodeTypeKind.Namespace)
                {
                    // A namespace is a container: what it declares is found under its name.
                    var groups = BuildFunctionGroups(key, type.Name, members, isEntry);
                    symbols.AddRange(groups);
                    namespaces[type.Name] = namespaces.GetValueOrDefault(type.Name) + groups.Count;
                    continue;
                }

                var typeSymbol = BuildType(key, module, type, members, known, isEntry);
                symbols.Add(typeSymbol);
                namespaces[module] = namespaces.GetValueOrDefault(module) + 1;
                if (type.Kind is CodeTypeKind.Class or CodeTypeKind.Interface)
                {
                    symbols.AddRange(BuildMethodGroups(key, module, type, members, typeSymbol.Prominence));
                }
            }

            var functions = BuildFunctionGroups(key, module, file.Members.Where(m => m.TypeIndex is null).ToList(), isEntry);
            if (functions.Count > 0)
            {
                symbols.AddRange(functions);
                namespaces[module] = namespaces.GetValueOrDefault(module) + functions.Count;
            }
        }

        var unique = symbols.DistinctBy(s => s.Sid, StringComparer.Ordinal).ToList();
        if (unique.Count > MaxSymbols)
        {
            unique = [.. unique.OrderByDescending(s => s.Prominence).ThenBy(s => s.Sid, StringComparer.Ordinal).Take(MaxSymbols)];
        }

        return new SymbolBuild(unique, namespaces);
    }

    private NuGetSymbolEntry BuildType(NuGetPackageKey key, string module, CodeTypeEntry type, List<CodeMemberEntry> members, Dictionary<string, KnownType> known, bool isEntry)
    {
        var fullName = QualifiedName(module, type);
        var sid = SymbolDocumentFactory.Sid(key, fullName);
        var publicMembers = members.Where(m => IsPublic(m.Modifiers)).ToList();
        var constructors = publicMembers.Where(m => m.Kind == CodeMemberKind.Constructor).Select(m => m.Signature).Distinct(StringComparer.Ordinal).ToList();
        var groups = MethodGroups(publicMembers);
        var properties = publicMembers
            .Where(m => m.Kind is CodeMemberKind.Property or CodeMemberKind.Field or CodeMemberKind.Indexer)
            .OrderBy(m => m.Name, StringComparer.Ordinal)
            .ToList();
        var enumValues = publicMembers.Where(m => m.Kind == CodeMemberKind.EnumMember).Select(m => m.Signature).ToList();

        var keyword = Keyword(type.Kind);
        var counts = type.Kind == CodeTypeKind.Enum
            ? string.Create(CultureInfo.InvariantCulture, $"{enumValues.Count} values")
            : string.Create(CultureInfo.InvariantCulture, $"{constructors.Count} ctor, {groups.Count} methods, {properties.Count} props");
        var summary = type.DocSummary ?? string.Empty;
        var bases = string.IsNullOrEmpty(type.BaseList) ? string.Empty : " extends " + type.BaseList;
        var card = $"{keyword} {fullName}{bases} \u2014 {counts}" + (summary.Length > 0 ? " \u00b7 " + summary : string.Empty);

        var words = new List<string>();
        words.AddRange(SymbolText.Tokenize(type.Name));
        words.AddRange(SymbolText.Tokenize(module));
        words.AddRange(properties.Take(MaxTokenNames).SelectMany(p => SymbolText.Tokenize(p.Name)));
        words.AddRange(groups.Take(MaxTokenNames).SelectMany(g => SymbolText.Tokenize(g[0].Name)));

        return new NuGetSymbolEntry(
            sid,
            ToSymbolKind(type.Kind),
            type.Name,
            fullName,
            module,
            null,
            $"{keyword} {fullName}",
            SymbolFormatting.Truncate(card, options.MaxCardChars),
            TypeProminence(type, groups.Count, constructors.Count, summary.Length > 0, isEntry),
            JoinTokens(words),
            SymbolFormatting.Truncate(summary, SummaryChars),
            RelatedLines(type, known, sid),
            SymbolFormatting.JoinLines(constructors, options.MaxLinesPerSection),
            SymbolFormatting.JoinLines(groups.Select(MethodGroupLine).ToList(), options.MaxLinesPerSection),
            SymbolFormatting.JoinLines(properties.Select(PropertyLine).ToList(), options.MaxLinesPerSection),
            SymbolFormatting.JoinLines(enumValues, options.MaxLinesPerSection),
            DocSection(summary));
    }

    private IEnumerable<NuGetSymbolEntry> BuildMethodGroups(NuGetPackageKey key, string module, CodeTypeEntry type, List<CodeMemberEntry> members, double typeProminence)
    {
        var fullName = QualifiedName(module, type);
        var typeSid = SymbolDocumentFactory.Sid(key, fullName);
        foreach (var group in MethodGroups(members.Where(m => IsPublic(m.Modifiers))))
        {
            var first = group[0];
            var full = $"{fullName}.{first.Name}";
            var summary = group.Select(m => m.DocSummary).FirstOrDefault(s => !string.IsNullOrEmpty(s)) ?? string.Empty;
            var overloads = group
                .Select(MethodLine)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(line => line.Length)
                .ThenBy(line => line, StringComparer.Ordinal)
                .Take(options.MaxOverloadsPerExpand)
                .ToList();
            var extra = group.Count - 1;
            var card = $"{type.Name}.{MethodLine(first)}" + (extra > 0 ? string.Create(CultureInfo.InvariantCulture, $" (+{extra})") : string.Empty);

            var words = new List<string>();
            words.AddRange(SymbolText.Tokenize(first.Name));
            words.AddRange(SymbolText.Tokenize(type.Name));

            yield return new NuGetSymbolEntry(
                SymbolDocumentFactory.Sid(key, full),
                NuGetSymbolKind.MethodGroup,
                first.Name,
                full,
                module,
                type.Name,
                $"method {full}",
                SymbolFormatting.Truncate(card, options.MaxCardChars),
                MethodProminence(typeProminence * 0.6, first.Name, summary.Length > 0),
                JoinTokens(words),
                SymbolFormatting.Truncate(summary, SummaryChars),
                $"{type.Name}={typeSid}",
                SymbolFormatting.JoinLines(overloads, options.MaxLinesPerSection),
                string.Empty,
                string.Empty,
                string.Empty,
                DocSection(summary));
        }
    }

    /// <summary>The functions and the exported constants of a module or namespace, one entry per name.</summary>
    private List<NuGetSymbolEntry> BuildFunctionGroups(NuGetPackageKey key, string module, List<CodeMemberEntry> members, bool isEntry)
    {
        var result = new List<NuGetSymbolEntry>();
        var groups = members
            .Where(m => (m.Kind is CodeMemberKind.Function or CodeMemberKind.Variable) && IsPublic(m.Modifiers))
            .GroupBy(m => m.Name, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal);
        foreach (var group in groups)
        {
            var items = group.ToList();
            var first = items[0];
            var full = $"{module}.{first.Name}";
            var summary = items.Select(m => m.DocSummary).FirstOrDefault(s => !string.IsNullOrEmpty(s)) ?? string.Empty;
            var overloads = items
                .Select(MethodLine)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(line => line.Length)
                .ThenBy(line => line, StringComparer.Ordinal)
                .Take(options.MaxOverloadsPerExpand)
                .ToList();
            var extra = items.Count - 1;
            var card = $"{module}.{MethodLine(first)}" + (extra > 0 ? string.Create(CultureInfo.InvariantCulture, $" (+{extra})") : string.Empty);

            var words = new List<string>();
            words.AddRange(SymbolText.Tokenize(first.Name));
            words.AddRange(SymbolText.Tokenize(module));
            var baseScore = (module.Contains('/', StringComparison.Ordinal) ? 60 : 85) + (isEntry ? EntryBonus : 0) - (first.Kind == CodeMemberKind.Variable ? 15 : 0);

            result.Add(new NuGetSymbolEntry(
                SymbolDocumentFactory.Sid(key, full),
                NuGetSymbolKind.MethodGroup,
                first.Name,
                full,
                module,
                module,
                first.Kind == CodeMemberKind.Variable ? $"const {full}" : $"function {full}",
                SymbolFormatting.Truncate(card, options.MaxCardChars),
                MethodProminence(baseScore, first.Name, summary.Length > 0),
                JoinTokens(words),
                SymbolFormatting.Truncate(summary, SummaryChars),
                string.Empty,
                SymbolFormatting.JoinLines(overloads, options.MaxLinesPerSection),
                string.Empty,
                string.Empty,
                string.Empty,
                DocSection(summary)));
        }

        return result;
    }

    private static List<List<CodeMemberEntry>> MethodGroups(IEnumerable<CodeMemberEntry> members) =>
        members
            .Where(m => m.Kind == CodeMemberKind.Method)
            .GroupBy(m => m.Name, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.ToList())
            .ToList();

    private static string MethodGroupLine(List<CodeMemberEntry> group)
    {
        var line = MethodLine(group[0]);
        return group.Count > 1 ? string.Create(CultureInfo.InvariantCulture, $"{line}   // +{group.Count - 1} overloads") : line;
    }

    /// <summary>The signature as the declaration reads, without the <c>function</c> keyword and with <c>static</c> / <c>abstract</c> kept: <c>static from(x: T): Box&lt;T&gt;</c>.</summary>
    private static string MethodLine(CodeMemberEntry member)
    {
        var signature = member.Signature;
        foreach (var prefix in new[] { "async function* ", "async function ", "function* ", "function " })
        {
            if (signature.StartsWith(prefix, StringComparison.Ordinal))
            {
                signature = (prefix.StartsWith("async", StringComparison.Ordinal) ? "async " : string.Empty) + signature[prefix.Length..];
                break;
            }
        }

        var modifiers = member.Modifiers?.Split(' ') ?? [];
        var prefixes = new StringBuilder();
        if (modifiers.Contains("static", StringComparer.Ordinal))
        {
            prefixes.Append("static ");
        }

        if (modifiers.Contains("abstract", StringComparer.Ordinal))
        {
            prefixes.Append("abstract ");
        }

        return prefixes + signature;
    }

    private static string PropertyLine(CodeMemberEntry member)
    {
        var modifiers = member.Modifiers?.Split(' ') ?? [];
        var prefix = (modifiers.Contains("static", StringComparer.Ordinal) ? "static " : string.Empty)
            + (modifiers.Contains("readonly", StringComparer.Ordinal) ? "readonly " : string.Empty);
        return prefix + member.Signature;
    }

    private static string RelatedLines(CodeTypeEntry type, Dictionary<string, KnownType> known, string ownSid) =>
        string.Join('\n', SplitBases(type.BaseList ?? string.Empty)
            .Select(SimpleName)
            .Distinct(StringComparer.Ordinal)
            .Where(known.ContainsKey)
            .Select(name => known[name])
            .Where(item => !string.Equals(item.Sid, ownSid, StringComparison.Ordinal))
            .Take(12)
            .Select(item => $"{item.Name}={item.Sid}"));

    private static IEnumerable<string> SplitBases(string text)
    {
        var depth = 0;
        var start = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            if (i < text.Length)
            {
                if (text[i] is '<' or '(' or '[' or '{')
                {
                    depth++;
                }
                else if (text[i] is '>' or ')' or ']' or '}')
                {
                    depth = Math.Max(0, depth - 1);
                }

                if (text[i] != ',' || depth > 0)
                {
                    continue;
                }
            }

            var item = text[start..i].Trim();
            start = i + 1;
            if (item.Length > 0)
            {
                yield return item;
            }
        }
    }

    private static string SimpleName(string baseText)
    {
        var generic = baseText.IndexOf('<', StringComparison.Ordinal);
        var bare = generic < 0 ? baseText : baseText[..generic];
        return bare[(bare.LastIndexOf('.') + 1)..].Trim();
    }

    private static string DocSection(string summary) =>
        summary.Length == 0 ? string.Empty : SymbolFormatting.DocJson(new XmlDocEntry { Summary = summary });

    /// <summary>The module of a file: its path inside the package, with the root index named after the package.</summary>
    private static string ModuleOf(string relativePath, string packageName)
    {
        var module = TypeScriptModuleNames.FromPath(relativePath);
        return module is "index" or "" ? packageName : module;
    }

    private static string QualifiedName(string module, CodeTypeEntry type) => $"{module}.{type.Name}";

    private static double TypeProminence(CodeTypeEntry type, int methodGroups, int constructors, bool hasSummary, bool isEntry)
    {
        var score = 100 + Math.Min(45, methodGroups * 0.65);
        if (constructors > 0)
        {
            score += 12;
        }

        if (hasSummary)
        {
            score += 10;
        }

        if (isEntry)
        {
            score += EntryBonus;
        }

        if (type.Kind is CodeTypeKind.Enum or CodeTypeKind.TypeAlias)
        {
            score -= 15;
        }

        if (methodGroups == 0 && constructors == 0 && type.Kind == CodeTypeKind.Class)
        {
            score -= 15;
        }

        return Math.Clamp(score, 1, 400);
    }

    private static double MethodProminence(double baseScore, string name, bool hasSummary)
    {
        var score = baseScore;
        if (ActionPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            score += 8;
        }

        if (hasSummary)
        {
            score += 8;
        }

        return Math.Clamp(score, 1, 400);
    }

    private string JoinTokens(IEnumerable<string> words)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var text = new StringBuilder();
        foreach (var word in words)
        {
            if (!seen.Add(word))
            {
                continue;
            }

            if (text.Length + word.Length + 1 > options.MaxTokenFieldChars)
            {
                break;
            }

            if (text.Length > 0)
            {
                text.Append(' ');
            }

            text.Append(word);
        }

        return text.ToString();
    }

    private static string Keyword(CodeTypeKind kind) => kind switch
    {
        CodeTypeKind.Interface => "interface",
        CodeTypeKind.Enum => "enum",
        CodeTypeKind.TypeAlias => "type",
        _ => "class",
    };

    private static NuGetSymbolKind ToSymbolKind(CodeTypeKind kind) => kind switch
    {
        CodeTypeKind.Interface => NuGetSymbolKind.Interface,
        CodeTypeKind.Enum => NuGetSymbolKind.Enum,
        CodeTypeKind.TypeAlias => NuGetSymbolKind.Struct,
        _ => NuGetSymbolKind.Class,
    };

    private static bool IsPublic(string? modifiers) =>
        modifiers is not null && modifiers.Split(' ').Contains("public", StringComparer.Ordinal);

    /// <summary>A module is public when no part of its path starts with an underscore.</summary>
    private static bool IsPublicModule(string module) =>
        module.Length > 0 && module.Split('/').All(part => !part.StartsWith('_'));
}
