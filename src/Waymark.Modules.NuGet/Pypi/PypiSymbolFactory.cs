using System.Globalization;
using System.Text;
using Waymark.Core.Code;
using Waymark.Core.NuGetIndex;
using Waymark.Languages.Python;
using Waymark.Modules.NuGet.Indexing;

namespace Waymark.Modules.NuGet.Pypi;

/// <summary>
/// Turns the indexed Python files of one package into the same entries the NuGet index holds: one per public class and one per method group or
/// module-level function. Deterministic: the same files always give the same ids. Sections follow the NuGet ones: constructors, methods, properties,
/// enum values. A name is public when it does not start with an underscore and, in a module that has <c>__all__</c>, is listed in it.
/// </summary>
internal sealed class PypiSymbolFactory(NuGetIndexOptions options)
{
    private const int MaxSymbols = 40_000;
    private const int MaxTokenNames = 80;
    private const int SummaryChars = 400;

    private static readonly HashSet<string> ReadableDunders = new(StringComparer.Ordinal) { "__call__", "__enter__", "__exit__", "__aenter__", "__aexit__" };

    private static readonly string[] ActionPrefixes =
    [
        "get", "create", "send", "add", "build", "run", "start", "open", "connect", "parse", "read", "write", "post", "put", "delete", "list", "query", "fetch", "load", "save",
    ];

    private sealed record KnownType(string Name, string Sid, string FullName);

    /// <param name="files">One entry per file; the path in each is the module path (<c>requests/api.py</c>), which names its module.</param>
    public SymbolBuild Build(NuGetPackageKey key, IReadOnlyList<CodeFileIndex> files)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(files);
        var modules = files
            .Select(file => (File: file, Module: PythonModuleNames.FromPath(file.Stamp.RelativePath)))
            .Where(item => IsPublicModule(item.Module))
            .OrderBy(item => item.Module, StringComparer.Ordinal)
            .ThenBy(item => item.File.Stamp.RelativePath, StringComparer.Ordinal)
            .ToList();

        var known = new Dictionary<string, KnownType>(StringComparer.Ordinal);
        foreach (var (file, _) in modules)
        {
            foreach (var type in file.Types.Where(IsPublicType))
            {
                known.TryAdd(type.Name, new KnownType(type.Name, SymbolDocumentFactory.Sid(key, type.FullName), type.FullName));
            }
        }

        var symbols = new List<NuGetSymbolEntry>();
        var namespaces = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (file, module) in modules)
        {
            for (var index = 0; index < file.Types.Count; index++)
            {
                var type = file.Types[index];
                if (!IsPublicType(type))
                {
                    continue;
                }

                var members = file.Members.Where(m => m.TypeIndex == index).ToList();
                var typeSymbol = BuildType(key, module, type, members, known);
                symbols.Add(typeSymbol);
                namespaces[module] = namespaces.GetValueOrDefault(module) + 1;
                if (type.Kind != CodeTypeKind.Enum)
                {
                    symbols.AddRange(BuildMethodGroups(key, module, type, members, typeSymbol.Prominence));
                }
            }

            var functions = BuildFunctionGroups(key, module, file);
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

    private NuGetSymbolEntry BuildType(NuGetPackageKey key, string module, CodeTypeEntry type, List<CodeMemberEntry> members, Dictionary<string, KnownType> known)
    {
        var sid = SymbolDocumentFactory.Sid(key, type.FullName);
        var publicMembers = members.Where(m => IsPublic(m.Modifiers)).ToList();
        var constructors = publicMembers.Where(m => m.Kind == CodeMemberKind.Constructor).ToList();
        var groups = MethodGroups(publicMembers);
        var properties = publicMembers
            .Where(m => m.Kind is CodeMemberKind.Property or CodeMemberKind.Field)
            .OrderBy(m => m.Name, StringComparer.Ordinal)
            .ToList();
        var enumValues = publicMembers.Where(m => m.Kind == CodeMemberKind.EnumMember).Select(m => m.Signature).ToList();

        var signatures = constructors.Select(c => ConstructorLine(type.Name, c)).Distinct(StringComparer.Ordinal).ToList();
        if (signatures.Count == 0 && type.Kind == CodeTypeKind.Record && DataclassLine(type.Name, members) is { } generated)
        {
            signatures.Add(generated);
        }

        var keyword = Keyword(type.Kind);
        var counts = type.Kind == CodeTypeKind.Enum
            ? string.Create(CultureInfo.InvariantCulture, $"{enumValues.Count} values")
            : string.Create(CultureInfo.InvariantCulture, $"{signatures.Count} ctor, {groups.Count} methods, {properties.Count} props");
        var summary = type.DocSummary ?? string.Empty;
        var card = $"{keyword} {type.FullName} \u2014 {counts}" + (summary.Length > 0 ? " \u00b7 " + summary : string.Empty);

        var words = new List<string>();
        words.AddRange(SymbolText.Tokenize(type.Name));
        words.AddRange(SymbolText.Tokenize(module));
        words.AddRange(properties.Take(MaxTokenNames).SelectMany(p => SymbolText.Tokenize(p.Name)));
        words.AddRange(groups.Take(MaxTokenNames).SelectMany(g => SymbolText.Tokenize(g[0].Name)));

        return new NuGetSymbolEntry(
            sid,
            ToSymbolKind(type.Kind),
            type.Name,
            type.FullName,
            module,
            null,
            $"{keyword} {type.FullName}",
            SymbolFormatting.Truncate(card, options.MaxCardChars),
            TypeProminence(type, module, groups.Count, signatures.Count, summary.Length > 0),
            JoinTokens(words),
            SymbolFormatting.Truncate(summary, SummaryChars),
            RelatedLines(type, known, sid),
            SymbolFormatting.JoinLines(signatures, options.MaxLinesPerSection),
            SymbolFormatting.JoinLines(groups.Select(MethodGroupLine).ToList(), options.MaxLinesPerSection),
            SymbolFormatting.JoinLines(properties.Select(PropertyLine).ToList(), options.MaxLinesPerSection),
            SymbolFormatting.JoinLines(enumValues, options.MaxLinesPerSection),
            DocSection(summary));
    }

    private IEnumerable<NuGetSymbolEntry> BuildMethodGroups(NuGetPackageKey key, string module, CodeTypeEntry type, List<CodeMemberEntry> members, double typeProminence)
    {
        var declaring = DeclaringTypeName(type, module);
        var typeSid = SymbolDocumentFactory.Sid(key, type.FullName);
        foreach (var group in MethodGroups(members.Where(m => IsPublic(m.Modifiers))))
        {
            var first = group[0];
            var full = $"{type.FullName}.{first.Name}";
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
                declaring,
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

    private List<NuGetSymbolEntry> BuildFunctionGroups(NuGetPackageKey key, string module, CodeFileIndex file)
    {
        var result = new List<NuGetSymbolEntry>();
        var groups = file.Members
            .Where(m => m.TypeIndex is null && m.Kind == CodeMemberKind.Function && IsPublic(m.Modifiers))
            .GroupBy(m => m.Name, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal);
        foreach (var group in groups)
        {
            var members = group.ToList();
            var first = members[0];
            var full = $"{module}.{first.Name}";
            var summary = members.Select(m => m.DocSummary).FirstOrDefault(s => !string.IsNullOrEmpty(s)) ?? string.Empty;
            var overloads = members
                .Select(MethodLine)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(line => line.Length)
                .ThenBy(line => line, StringComparer.Ordinal)
                .Take(options.MaxOverloadsPerExpand)
                .ToList();
            var extra = members.Count - 1;
            var card = $"{module}.{MethodLine(first)}" + (extra > 0 ? string.Create(CultureInfo.InvariantCulture, $" (+{extra})") : string.Empty);

            var words = new List<string>();
            words.AddRange(SymbolText.Tokenize(first.Name));
            words.AddRange(SymbolText.Tokenize(module));

            result.Add(new NuGetSymbolEntry(
                SymbolDocumentFactory.Sid(key, full),
                NuGetSymbolKind.MethodGroup,
                first.Name,
                full,
                module,
                module,
                $"function {full}",
                SymbolFormatting.Truncate(card, options.MaxCardChars),
                MethodProminence(module.Contains('.', StringComparison.Ordinal) ? 60 : 85, first.Name, summary.Length > 0),
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
            .Where(m => m.Kind == CodeMemberKind.Method && IsReadable(m.Name))
            .GroupBy(m => m.Name, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.ToList())
            .ToList();

    private static string MethodGroupLine(List<CodeMemberEntry> group)
    {
        var line = MethodLine(group[0]);
        return group.Count > 1 ? string.Create(CultureInfo.InvariantCulture, $"{line}   # +{group.Count - 1} overloads") : line;
    }

    /// <summary><c>async name(params) \u2192 Return</c>; a method has no <c>self</c> or <c>cls</c> in it.</summary>
    private static string MethodLine(CodeMemberEntry member)
    {
        var parsed = PythonSignatureText.Parse(member.Signature);
        if (parsed is null)
        {
            return member.Signature;
        }

        var parameters = member.TypeIndex is null ? parsed.Parameters : PythonSignatureText.WithoutReceiver(parsed.Parameters);
        var isStatic = member.Modifiers?.Contains("static", StringComparison.Ordinal) ?? false;
        var returns = parsed.Returns is null ? string.Empty : " \u2192 " + parsed.Returns;
        return $"{(isStatic ? "static " : string.Empty)}{(parsed.IsAsync ? "async " : string.Empty)}{parsed.Name}({parameters}){returns}";
    }

    private static string ConstructorLine(string className, CodeMemberEntry constructor)
    {
        var parsed = PythonSignatureText.Parse(constructor.Signature);
        return parsed is null ? className + "()" : $"{className}({PythonSignatureText.WithoutReceiver(parsed.Parameters)})";
    }

    /// <summary>A dataclass or a model has no __init__ in its source; its constructor takes its annotated fields.</summary>
    private static string? DataclassLine(string className, IEnumerable<CodeMemberEntry> members)
    {
        var fields = members
            .Where(m => m.Kind == CodeMemberKind.Field
                && m.ReturnType is not null
                && !m.ReturnType.StartsWith("ClassVar", StringComparison.Ordinal)
                && !m.Signature.StartsWith("self.", StringComparison.Ordinal)
                && IsPublic(m.Modifiers))
            .Select(m => m.Signature)
            .ToList();
        return fields.Count == 0 ? null : $"{className}({string.Join(", ", fields)})";
    }

    private static string PropertyLine(CodeMemberEntry member)
    {
        if (member.Kind == CodeMemberKind.Property)
        {
            var type = PythonSignatureText.Parse(member.Signature)?.Returns ?? member.ReturnType;
            return type is null ? $"{member.Name}  (property)" : $"{member.Name}: {type}  (property)";
        }

        return member.Signature.StartsWith("self.", StringComparison.Ordinal) ? member.Signature[5..] : member.Signature;
    }

    private static string RelatedLines(CodeTypeEntry type, Dictionary<string, KnownType> known, string ownSid) =>
        string.Join('\n', PythonSignatureText.SplitTopLevel(type.BaseList ?? string.Empty)
            .Select(PythonSignatureText.SimpleName)
            .Distinct(StringComparer.Ordinal)
            .Where(known.ContainsKey)
            .Select(name => known[name])
            .Where(item => !string.Equals(item.Sid, ownSid, StringComparison.Ordinal))
            .Take(12)
            .Select(item => $"{item.Name}={item.Sid}"));

    /// <summary>The documentation JSON of the doc section: the first line of the docstring.</summary>
    private static string DocSection(string summary) =>
        summary.Length == 0 ? string.Empty : SymbolFormatting.DocJson(new XmlDocEntry { Summary = summary });

    /// <summary>The type name without its module: <c>Outer.Inner</c> for <c>pkg.mod.Outer.Inner</c>. The explorer filters method groups by exactly this text.</summary>
    private static string DeclaringTypeName(CodeTypeEntry type, string module) =>
        module.Length > 0 && type.FullName.StartsWith(module + ".", StringComparison.Ordinal) ? type.FullName[(module.Length + 1)..] : type.FullName;

    private static double TypeProminence(CodeTypeEntry type, string module, int methodGroups, int constructors, bool hasSummary)
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

        if (!module.Contains('.', StringComparison.Ordinal))
        {
            score += 12;
        }

        if (type.Kind == CodeTypeKind.Enum)
        {
            score -= 15;
        }

        if (methodGroups == 0 && constructors == 0)
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
        _ => "class",
    };

    private static NuGetSymbolKind ToSymbolKind(CodeTypeKind kind) => kind switch
    {
        CodeTypeKind.Interface => NuGetSymbolKind.Interface,
        CodeTypeKind.Enum => NuGetSymbolKind.Enum,
        _ => NuGetSymbolKind.Class,
    };

    private static bool IsPublic(string? modifiers) =>
        modifiers is not null && modifiers.Split(' ').Contains("public", StringComparer.Ordinal);

    private static bool IsPublicType(CodeTypeEntry type) => type.Kind != CodeTypeKind.TypeAlias && IsPublic(type.Modifiers);

    /// <summary>A module is public when no part of its dotted path starts with an underscore.</summary>
    private static bool IsPublicModule(string module) =>
        module.Length > 0 && module.Split('.').All(part => !part.StartsWith('_'));

    /// <summary>A dunder method is noise except the few that people call or write with: calling an object, <c>with</c> blocks.</summary>
    private static bool IsReadable(string name) =>
        !(name.StartsWith("__", StringComparison.Ordinal) && name.EndsWith("__", StringComparison.Ordinal)) || ReadableDunders.Contains(name);
}

/// <summary>Reads the text of a function signature as the code index stores it: <c>[async ]def name(params)[ -&gt; Return]</c>.</summary>
internal static class PythonSignatureText
{
    internal sealed record Parsed(bool IsAsync, string Name, string Parameters, string? Returns);

    public static Parsed? Parse(string signature)
    {
        var text = signature.Trim();
        var isAsync = text.StartsWith("async ", StringComparison.Ordinal);
        if (isAsync)
        {
            text = text[6..];
        }

        if (!text.StartsWith("def ", StringComparison.Ordinal))
        {
            return null;
        }

        text = text[4..];
        var open = text.IndexOf('(', StringComparison.Ordinal);
        if (open < 0)
        {
            return null;
        }

        var bracket = text.IndexOf('[', StringComparison.Ordinal);
        var nameEnd = bracket >= 0 && bracket < open ? bracket : open;
        var depth = 0;
        var close = -1;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '(')
            {
                depth++;
            }
            else if (text[i] == ')' && --depth == 0)
            {
                close = i;
                break;
            }
        }

        if (close < 0)
        {
            return null;
        }

        var rest = text[(close + 1)..].Trim();
        var returns = rest.StartsWith("->", StringComparison.Ordinal) ? rest[2..].Trim() : null;
        return new Parsed(isAsync, text[..nameEnd].Trim(), text[(open + 1)..close].Trim(), string.IsNullOrEmpty(returns) ? null : returns);
    }

    /// <summary>The parameter list without a leading <c>self</c> or <c>cls</c>.</summary>
    public static string WithoutReceiver(string parameters)
    {
        var items = SplitTopLevel(parameters);
        if (items.Count > 0)
        {
            var first = items[0];
            var end = first.IndexOfAny([':', '=']);
            var name = (end < 0 ? first : first[..end]).Trim();
            if (name is "self" or "cls")
            {
                items.RemoveAt(0);
            }
        }

        return string.Join(", ", items);
    }

    /// <summary>Splits at the commas that are not inside brackets, parentheses or braces.</summary>
    public static List<string> SplitTopLevel(string text)
    {
        var items = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            if (i < text.Length)
            {
                if (text[i] is '(' or '[' or '{')
                {
                    depth++;
                }
                else if (text[i] is ')' or ']' or '}')
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
                items.Add(item);
            }
        }

        return items;
    }

    /// <summary>The name of a base class without its module or type arguments.</summary>
    public static string SimpleName(string baseText)
    {
        var bracket = baseText.IndexOf('[', StringComparison.Ordinal);
        var bare = bracket < 0 ? baseText : baseText[..bracket];
        return bare[(bare.LastIndexOf('.') + 1)..].Trim();
    }
}
