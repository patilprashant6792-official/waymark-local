using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Waymark.Modules.NuGet.Loading;

namespace Waymark.Modules.NuGet.Indexing;

/// <summary>Renders reflected members as the short C#-like lines that fill the stored detail sections.</summary>
internal static partial class SymbolFormatting
{
    private const int SummaryLimit = 600;
    private const int ReturnsLimit = 300;
    private const int RemarksLimit = 400;
    private const int MaxDocParams = 16;
    private const int ParamLimit = 200;
    private const int MaxDocExceptions = 8;
    private const int ExceptionLimit = 160;

    private static readonly JsonSerializerOptions DocJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    public static bool IsPublic(string visibility) => string.Equals(visibility, "public", StringComparison.OrdinalIgnoreCase);

    public static string TypeText(string rawType) => TypeNameFormatter.Simplify(rawType);

    public static string ParameterList(IReadOnlyList<ReflectedParameter> parameters) =>
        string.Join(", ", parameters.Select(p => $"{TypeText(p.ParameterType)} {p.Name}"));

    public static string ParameterTypes(IReadOnlyList<ReflectedParameter> parameters) =>
        string.Join(", ", parameters.Select(p => TypeText(p.ParameterType)));

    public static string MethodLine(ReflectedMethod method) =>
        $"{(method.IsStatic ? "static " : string.Empty)}{method.Name}({ParameterList(method.Parameters)}) \u2192 {TypeText(method.ReturnType)}";

    public static string ConstructorLine(string displayName, ReflectedConstructor constructor) =>
        $"{displayName}({ParameterList(constructor.Parameters)})";

    public static string PropertyLine(ReflectedProperty property)
    {
        var accessors = (property.CanRead ? "get; " : string.Empty) + (property.CanWrite ? "set; " : string.Empty);
        return $"{(property.IsStatic ? "static " : string.Empty)}{TypeText(property.PropertyType)} {property.Name} {{ {accessors}}}";
    }

    public static string FieldLine(ReflectedField field)
    {
        var modifiers = (field.IsStatic ? "static " : string.Empty) + (field.IsReadOnly ? "readonly " : string.Empty);
        return $"{modifiers}{TypeText(field.FieldType)} {field.Name}";
    }

    public static string EventLine(ReflectedEvent reflectedEvent) =>
        $"{(reflectedEvent.IsStatic ? "static " : string.Empty)}event {TypeText(reflectedEvent.EventType)} {reflectedEvent.Name}";

    /// <summary>Converts a CLR arity suffix (Foo`2) to the way C# spells it (Foo&lt;T, U&gt;); other names pass through.</summary>
    public static string DisplayName(string typeName) =>
        typeName.Contains('`', StringComparison.Ordinal) ? TypeNameFormatter.Simplify(typeName) : typeName;

    /// <summary>The unqualified name without generic arguments or arity: Outer.Inner&lt;T&gt; becomes Inner.</summary>
    public static string SimpleName(string typeName)
    {
        var end = typeName.IndexOfAny(['<', '`']);
        var bare = end >= 0 ? typeName[..end] : typeName;
        var dot = bare.LastIndexOf('.');
        return dot >= 0 ? bare[(dot + 1)..] : bare;
    }

    public static IEnumerable<string> Identifiers(string typeText) =>
        IdentifierPattern().Matches(typeText).Select(m => m.Value);

    public static string Truncate(string text, int maxChars)
    {
        if (text.Length <= maxChars)
        {
            return text;
        }

        return maxChars <= 1 ? text[..maxChars] : text[..(maxChars - 1)].TrimEnd() + "\u2026";
    }

    public static string JoinLines(IReadOnlyList<string> lines, int maxLines)
    {
        if (lines.Count == 0)
        {
            return string.Empty;
        }

        if (lines.Count <= maxLines)
        {
            return string.Join('\n', lines);
        }

        var kept = lines.Take(Math.Max(1, maxLines - 1)).ToList();
        kept.Add(string.Create(CultureInfo.InvariantCulture, $"// \u2026 {lines.Count - kept.Count} more"));
        return string.Join('\n', kept);
    }

    /// <summary>The lite documentation JSON stored in the doc section: short fields only, empty when nothing is documented.</summary>
    public static string DocJson(XmlDocEntry? entry)
    {
        if (entry is null || entry.IsEmpty)
        {
            return string.Empty;
        }

        var document = new Dictionary<string, object>(StringComparer.Ordinal);
        AddText(document, "summary", entry.Summary, SummaryLimit);
        AddText(document, "returns", entry.Returns, ReturnsLimit);
        AddText(document, "remarks", entry.Remarks, RemarksLimit);
        AddMap(document, "params", entry.Params, MaxDocParams, ParamLimit);
        AddMap(document, "exceptions", entry.Exceptions, MaxDocExceptions, ExceptionLimit);
        return document.Count == 0 ? string.Empty : JsonSerializer.Serialize(document, DocJsonOptions);
    }

    private static void AddText(Dictionary<string, object> document, string key, string? text, int limit)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            document[key] = Truncate(text, limit);
        }
    }

    private static void AddMap(Dictionary<string, object> document, string key, Dictionary<string, string> source, int maxEntries, int limit)
    {
        var entries = source
            .Where(p => !string.IsNullOrWhiteSpace(p.Value))
            .Take(maxEntries)
            .ToDictionary(p => p.Key, p => Truncate(p.Value, limit), StringComparer.Ordinal);
        if (entries.Count > 0)
        {
            document[key] = entries;
        }
    }

    [GeneratedRegex(@"[A-Za-z_][A-Za-z0-9_]*", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex IdentifierPattern();
}
