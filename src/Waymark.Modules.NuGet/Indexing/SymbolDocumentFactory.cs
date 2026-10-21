using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Waymark.Core.NuGetIndex;
using Waymark.Modules.NuGet.Loading;

namespace Waymark.Modules.NuGet.Indexing;

/// <summary>
/// Turns the reflected types of one package into index entries: one per public type and one per method group.
/// Pure and deterministic: the same input always yields the same ids, so a rebuild keeps ids stable.
/// </summary>
internal sealed partial class SymbolDocumentFactory(NuGetIndexOptions options)
{
    private const string GlobalNamespaceLabel = "(global)";
    private const int MaxTokenProperties = 80;
    private const int MaxTokenMethods = 120;
    private const int MaxTokenParameterTypes = 24;

    private static readonly HashSet<string> ObjectMethodNames = new(StringComparer.Ordinal)
    {
        "Equals", "GetHashCode", "ToString", "GetType", "MemberwiseClone", "Finalize", "ReferenceEquals"
    };

    private static readonly string[] AccessorPrefixes = ["get_", "set_", "add_", "remove_", "op_"];

    private readonly NuGetIndexOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    public SymbolBuild Build(NuGetPackageKey key, IReadOnlyList<ReflectedType> types, IReadOnlyDictionary<string, XmlDocEntry> docs)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(types);
        ArgumentNullException.ThrowIfNull(docs);

        var unique = types
            .Where(t => !string.IsNullOrWhiteSpace(t.TypeName))
            .DistinctBy(FullName, StringComparer.Ordinal)
            .ToList();

        var known = IndexKnownTypes(key, unique);
        var methodDocs = IndexMethodDocs(docs);
        var neighbours = unique.ToDictionary(FullName, t => ReferencedTypes(t, known), StringComparer.Ordinal);
        var inbound = CountInbound(neighbours, known);

        var symbols = new List<NuGetSymbolEntry>(unique.Count * 3);
        var namespaces = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var type in unique)
        {
            var full = FullName(type);
            var typeDoc = docs.GetValueOrDefault(DocTypeId(type));
            var context = new TypeContext(key, type, full, Sid(key, full), typeDoc, neighbours[full], known);
            var typeSymbol = BuildTypeSymbol(context, inbound.GetValueOrDefault(full));
            symbols.Add(typeSymbol);

            var label = type.Namespace.Length == 0 ? GlobalNamespaceLabel : type.Namespace;
            namespaces[label] = namespaces.GetValueOrDefault(label) + 1;

            if (type.Kind is ReflectedTypeKind.Enum or ReflectedTypeKind.Delegate)
            {
                continue;
            }

            symbols.AddRange(BuildMethodGroups(context, typeSymbol.Prominence, methodDocs));
        }

        return new SymbolBuild(symbols.DistinctBy(s => s.Sid, StringComparer.Ordinal).ToList(), namespaces);
    }

    internal static string Sid(NuGetPackageKey key, string fullName)
    {
        var text = $"{key.PackageId}|{key.Version}|{key.TargetFramework}|{fullName}".ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexStringLower(hash.AsSpan(0, 6));
    }

    private static string FullName(ReflectedType type) =>
        type.Namespace.Length == 0 ? type.TypeName : type.Namespace + "." + type.TypeName;

    private static Dictionary<string, KnownType> IndexKnownTypes(NuGetPackageKey key, List<ReflectedType> types)
    {
        var known = new Dictionary<string, KnownType>(StringComparer.Ordinal);
        foreach (var type in types)
        {
            var simple = SymbolFormatting.SimpleName(type.TypeName);
            known.TryAdd(simple, new KnownType(simple, Sid(key, FullName(type)), FullName(type)));
        }

        return known;
    }

    private static HashSet<string> ReferencedTypes(ReflectedType type, Dictionary<string, KnownType> known)
    {
        var own = SymbolFormatting.SimpleName(type.TypeName);
        var rawTypes = new List<string>();
        foreach (var method in type.Methods.Where(m => SymbolFormatting.IsPublic(m.Visibility)))
        {
            rawTypes.Add(method.ReturnType);
            rawTypes.AddRange(method.Parameters.Select(p => p.ParameterType));
        }

        foreach (var constructor in type.Constructors.Where(c => SymbolFormatting.IsPublic(c.Visibility)))
        {
            rawTypes.AddRange(constructor.Parameters.Select(p => p.ParameterType));
        }

        rawTypes.AddRange(type.Properties.Where(p => SymbolFormatting.IsPublic(p.Visibility)).Select(p => p.PropertyType));

        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in rawTypes)
        {
            foreach (var identifier in SymbolFormatting.Identifiers(SymbolFormatting.TypeText(raw)))
            {
                if (!string.Equals(identifier, own, StringComparison.Ordinal) && known.ContainsKey(identifier))
                {
                    result.Add(identifier);
                }
            }
        }

        return result;
    }

    private static Dictionary<string, int> CountInbound(Dictionary<string, HashSet<string>> neighbours, Dictionary<string, KnownType> known)
    {
        var inbound = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var referenced in neighbours.Values)
        {
            foreach (var simple in referenced)
            {
                var full = known[simple].FullName;
                inbound[full] = inbound.GetValueOrDefault(full) + 1;
            }
        }

        return inbound;
    }

    private NuGetSymbolEntry BuildTypeSymbol(TypeContext context, int inboundReferences)
    {
        var type = context.Type;
        var simple = SymbolFormatting.SimpleName(type.TypeName);
        var display = SymbolFormatting.DisplayName(type.TypeName);
        var publicConstructors = type.Constructors.Where(c => SymbolFormatting.IsPublic(c.Visibility)).ToList();
        var groups = PublicMethodGroups(type);
        var properties = type.Properties.Where(p => SymbolFormatting.IsPublic(p.Visibility)).OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
        var summary = context.Doc?.Summary ?? string.Empty;
        var keyword = Keyword(type.Kind);

        var signature = publicConstructors.Select(c => SymbolFormatting.ConstructorLine(display, c)).Distinct(StringComparer.Ordinal).ToList();
        var methods = groups.Select(MethodGroupLine).ToList();
        var props = properties.Select(SymbolFormatting.PropertyLine).ToList();
        var data = DataLines(type);

        var counts = type.Kind == ReflectedTypeKind.Enum
            ? string.Create(CultureInfo.InvariantCulture, $"{data.Count} values")
            : string.Create(CultureInfo.InvariantCulture, $"{publicConstructors.Count} ctor, {groups.Count} methods, {properties.Count} props");
        var card = $"{keyword} {context.FullName} \u2014 {counts}" + (summary.Length > 0 ? " \u00b7 " + summary : string.Empty);

        return new NuGetSymbolEntry(
            context.Sid,
            ToSymbolKind(type.Kind),
            display,
            context.FullName,
            type.Namespace,
            null,
            $"{keyword} {context.FullName}",
            SymbolFormatting.Truncate(card, _options.MaxCardChars),
            TypeProminence(type, simple, inboundReferences, summary.Length > 0, groups.Count, publicConstructors.Count, properties.Count),
            TypeTokens(type, groups, properties),
            SymbolFormatting.Truncate(summary, 400),
            RelatedLines(context.Neighbours, context.Known, context.Sid),
            SymbolFormatting.JoinLines(signature, _options.MaxLinesPerSection),
            SymbolFormatting.JoinLines(methods, _options.MaxLinesPerSection),
            SymbolFormatting.JoinLines(props, _options.MaxLinesPerSection),
            SymbolFormatting.JoinLines(data, _options.MaxLinesPerSection),
            SymbolFormatting.DocJson(context.Doc));
    }

    private IEnumerable<NuGetSymbolEntry> BuildMethodGroups(TypeContext context, double typeProminence, Dictionary<string, XmlDocEntry> methodDocs)
    {
        var type = context.Type;
        var simple = SymbolFormatting.SimpleName(type.TypeName);
        var docPath = DocTypeId(type)[2..];

        foreach (var group in PublicMethodGroups(type))
        {
            var first = group[0];
            var full = context.FullName + "." + first.Name;
            var doc = group.Select(m => methodDocs.GetValueOrDefault(docPath + "." + m.Name)).FirstOrDefault(d => d is not null);
            var summary = doc?.Summary ?? string.Empty;
            var overloads = group.Select(SymbolFormatting.MethodLine).Distinct(StringComparer.Ordinal)
                .OrderBy(l => l.Length).ThenBy(l => l, StringComparer.Ordinal).Take(_options.MaxOverloadsPerExpand).ToList();
            var extra = group.Count - 1;
            var card = $"{simple}.{first.Name}({SymbolFormatting.ParameterTypes(first.Parameters)}) \u2192 {SymbolFormatting.TypeText(first.ReturnType)}"
                + (extra > 0 ? string.Create(CultureInfo.InvariantCulture, $" (+{extra})") : string.Empty);

            yield return new NuGetSymbolEntry(
                Sid(context.Key, full),
                NuGetSymbolKind.MethodGroup,
                first.Name,
                full,
                type.Namespace,
                type.TypeName,
                $"method {full}",
                SymbolFormatting.Truncate(card, _options.MaxCardChars),
                MethodProminence(typeProminence, first, doc is not null && summary.Length > 0),
                MethodTokens(first, simple, group),
                SymbolFormatting.Truncate(summary, 400),
                MethodRelated(group, context),
                SymbolFormatting.JoinLines(overloads, _options.MaxLinesPerSection),
                string.Empty,
                string.Empty,
                string.Empty,
                SymbolFormatting.DocJson(doc));
        }
    }

    private static List<List<ReflectedMethod>> PublicMethodGroups(ReflectedType type) =>
        type.Methods
            .Where(m => SymbolFormatting.IsPublic(m.Visibility) && !IsNoiseMethod(m.Name))
            .GroupBy(m => m.Name, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.ToList())
            .ToList();

    private static bool IsNoiseMethod(string name) =>
        name.StartsWith('<') || ObjectMethodNames.Contains(name) || AccessorPrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal));

    private static string MethodGroupLine(List<ReflectedMethod> group)
    {
        var line = SymbolFormatting.MethodLine(group[0]);
        return group.Count > 1 ? string.Create(CultureInfo.InvariantCulture, $"{line}   // +{group.Count - 1} overloads") : line;
    }

    private static List<string> DataLines(ReflectedType type)
    {
        if (type.Kind == ReflectedTypeKind.Enum)
        {
            return type.Fields.Where(f => SymbolFormatting.IsPublic(f.Visibility) && f.IsStatic).Select(f => f.Name).ToList();
        }

        var lines = type.Fields.Where(f => SymbolFormatting.IsPublic(f.Visibility)).Select(SymbolFormatting.FieldLine).OrderBy(l => l, StringComparer.Ordinal).ToList();
        lines.AddRange(type.Events.Where(e => SymbolFormatting.IsPublic(e.Visibility)).Select(SymbolFormatting.EventLine).OrderBy(l => l, StringComparer.Ordinal));
        return lines;
    }

    private string TypeTokens(ReflectedType type, List<List<ReflectedMethod>> groups, List<ReflectedProperty> properties)
    {
        var words = new List<string>();
        words.AddRange(SymbolText.Tokenize(type.TypeName));
        words.AddRange(SymbolText.Tokenize(type.Namespace));
        words.AddRange(properties.Take(MaxTokenProperties).SelectMany(p => SymbolText.Tokenize(p.Name)));
        words.AddRange(groups.Take(MaxTokenMethods).SelectMany(g => SymbolText.Tokenize(g[0].Name)));
        return JoinTokens(words);
    }

    private string MethodTokens(ReflectedMethod first, string typeSimpleName, List<ReflectedMethod> group)
    {
        var words = new List<string>();
        words.AddRange(SymbolText.Tokenize(first.Name));
        words.AddRange(SymbolText.Tokenize(typeSimpleName));
        words.AddRange(group.SelectMany(m => m.Parameters).Select(p => SymbolFormatting.TypeText(p.ParameterType)).Take(MaxTokenParameterTypes).SelectMany(SymbolText.Tokenize));
        return JoinTokens(words);
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

            if (text.Length + word.Length + 1 > _options.MaxTokenFieldChars)
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

    private string RelatedLines(HashSet<string> neighbours, Dictionary<string, KnownType> known, string ownSid) =>
        string.Join('\n', neighbours
            .Select(n => known[n])
            .Where(k => !string.Equals(k.Sid, ownSid, StringComparison.Ordinal))
            .OrderBy(k => k.Name, StringComparer.Ordinal)
            .Take(_options.MaxRelatedPerSymbol)
            .Select(k => $"{k.Name}={k.Sid}"));

    private string MethodRelated(List<ReflectedMethod> group, TypeContext context)
    {
        var own = SymbolFormatting.SimpleName(context.Type.TypeName);
        var names = new List<string> { own };
        foreach (var method in group)
        {
            foreach (var raw in method.Parameters.Select(p => p.ParameterType).Append(method.ReturnType))
            {
                names.AddRange(SymbolFormatting.Identifiers(SymbolFormatting.TypeText(raw)).Where(i => !string.Equals(i, own, StringComparison.Ordinal) && context.Known.ContainsKey(i)));
            }
        }

        return string.Join('\n', names
            .Distinct(StringComparer.Ordinal)
            .Take(_options.MaxRelatedPerSymbol)
            .Select(n => $"{n}={context.Known[n].Sid}"));
    }

    private static double TypeProminence(ReflectedType type, string simple, int inbound, bool hasSummary, int methodGroups, int publicConstructors, int properties)
    {
        var publicMethodCount = type.Methods.Count(m => SymbolFormatting.IsPublic(m.Visibility) && !IsNoiseMethod(m.Name));
        var score = 100 + (8 * Math.Log2(1 + inbound)) + Math.Min(45, publicMethodCount * 0.65);
        if (publicConstructors > 0)
        {
            score += 12;
        }

        if (EntryPointName().IsMatch(simple))
        {
            score += 12;
        }

        if (hasSummary)
        {
            score += 10;
        }

        if (type.Methods.Any(m => SymbolFormatting.IsPublic(m.Visibility) && m.IsStatic && ReturnsType(m, simple)))
        {
            score += 8;
        }

        if (NoiseName().IsMatch(simple))
        {
            score -= 30;
        }

        if (type.Kind == ReflectedTypeKind.Enum)
        {
            score -= 15;
        }

        if (methodGroups == 0 && properties == 0 && publicConstructors == 0)
        {
            score -= 25;
        }

        return Math.Clamp(score, 1, 400);
    }

    private static double MethodProminence(double typeProminence, ReflectedMethod first, bool hasSummary)
    {
        var score = typeProminence * 0.6;
        if (ActionMethodName().IsMatch(first.Name))
        {
            score += 8;
        }

        if (first.Name.EndsWith("Async", StringComparison.Ordinal))
        {
            score += 5;
        }

        if (hasSummary)
        {
            score += 8;
        }

        if (first.IsStatic)
        {
            score += 3;
        }

        return Math.Clamp(score, 1, 400);
    }

    private static bool ReturnsType(ReflectedMethod method, string simple) =>
        SymbolFormatting.Identifiers(SymbolFormatting.TypeText(method.ReturnType)).FirstOrDefault() is { } first
        && string.Equals(first, simple, StringComparison.Ordinal);

    private static string Keyword(ReflectedTypeKind kind) => kind switch
    {
        ReflectedTypeKind.Interface => "interface",
        ReflectedTypeKind.Enum => "enum",
        ReflectedTypeKind.Struct => "struct",
        ReflectedTypeKind.Delegate => "delegate",
        _ => "class"
    };

    private static NuGetSymbolKind ToSymbolKind(ReflectedTypeKind kind) => kind switch
    {
        ReflectedTypeKind.Interface => NuGetSymbolKind.Interface,
        ReflectedTypeKind.Enum => NuGetSymbolKind.Enum,
        ReflectedTypeKind.Struct => NuGetSymbolKind.Struct,
        ReflectedTypeKind.Delegate => NuGetSymbolKind.Delegate,
        _ => NuGetSymbolKind.Class
    };

    /// <summary>XML documentation id of a type: T:Namespace.Outer.Inner`2 (arity suffix, not angle brackets).</summary>
    private static string DocTypeId(ReflectedType type)
    {
        var name = type.TypeName;
        var arity = 0;
        var open = name.IndexOf('<', StringComparison.Ordinal);
        if (open >= 0)
        {
            var close = name.LastIndexOf('>');
            var arguments = close > open ? name[(open + 1)..close] : string.Empty;
            arity = arguments.Length == 0 ? 0 : arguments.Count(c => c == ',') + 1;
            name = name[..open];
        }

        var qualified = type.Namespace.Length == 0 ? name : type.Namespace + "." + name;
        return arity > 0 ? string.Create(CultureInfo.InvariantCulture, $"T:{qualified}`{arity}") : "T:" + qualified;
    }

    /// <summary>Method documentation by member path without parameters: M:Ns.Type.Method (first overload with docs wins).</summary>
    private static Dictionary<string, XmlDocEntry> IndexMethodDocs(IReadOnlyDictionary<string, XmlDocEntry> docs)
    {
        var index = new Dictionary<string, XmlDocEntry>(StringComparer.Ordinal);
        foreach (var (id, entry) in docs)
        {
            if (!id.StartsWith("M:", StringComparison.Ordinal))
            {
                continue;
            }

            var path = id[2..];
            var paren = path.IndexOf('(', StringComparison.Ordinal);
            if (paren >= 0)
            {
                path = path[..paren];
            }

            var lastDot = path.LastIndexOf('.');
            var generic = lastDot >= 0 ? path.IndexOf("``", lastDot, StringComparison.Ordinal) : -1;
            if (generic >= 0)
            {
                path = path[..generic];
            }

            index.TryAdd(path, entry);
        }

        return index;
    }

    [GeneratedRegex(@"(Client|Builder|Factory|Provider|Service|Extensions|Options|Context|Manager|Handler)$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex EntryPointName();

    [GeneratedRegex(@"(^_|^<|Internal|Impl$|Base$|Helper$|Attribute$|EventArgs$)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NoiseName();

    [GeneratedRegex(@"^(Create|Get|Send|Complete|Add|Use|Build|Execute|Run|Start|Open|Connect|Parse|From|With|Set|Query|List|Read|Write|Upload|Download)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ActionMethodName();

    private sealed record KnownType(string Name, string Sid, string FullName);

    private sealed record TypeContext(
        NuGetPackageKey Key,
        ReflectedType Type,
        string FullName,
        string Sid,
        XmlDocEntry? Doc,
        HashSet<string> Neighbours,
        Dictionary<string, KnownType> Known);
}
