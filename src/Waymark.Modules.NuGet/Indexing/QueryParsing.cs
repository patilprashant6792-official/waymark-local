using Waymark.Core.NuGetIndex;

namespace Waymark.Modules.NuGet.Indexing;

/// <summary>Reads the free-text arguments of the nuget_* tools (kind, sections, identifiers) into typed values.</summary>
internal static class QueryParsing
{
    private const int SidLength = 12;

    private static readonly Dictionary<string, NuGetSymbolKind> KindAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["class"] = NuGetSymbolKind.Class,
        ["type"] = NuGetSymbolKind.Class,
        ["interface"] = NuGetSymbolKind.Interface,
        ["enum"] = NuGetSymbolKind.Enum,
        ["struct"] = NuGetSymbolKind.Struct,
        ["delegate"] = NuGetSymbolKind.Delegate,
        ["method"] = NuGetSymbolKind.MethodGroup,
        ["methods"] = NuGetSymbolKind.MethodGroup,
        ["methodgroup"] = NuGetSymbolKind.MethodGroup
    };

    public static NuGetSymbolKind? ParseKind(string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind))
        {
            return null;
        }

        return KindAliases.TryGetValue(kind.Trim(), out var parsed)
            ? parsed
            : throw new ArgumentException("Unknown kind '" + kind.Trim() + "'. Use class, interface, enum, struct, delegate or method.", nameof(kind));
    }

    /// <summary>Comma separated section names, or 'all'. Unknown names are ignored; null means the caller should use the defaults.</summary>
    public static IReadOnlySet<string>? ParseSections(string? sections)
    {
        if (string.IsNullOrWhiteSpace(sections))
        {
            return null;
        }

        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var part in sections.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var name = part.ToLowerInvariant();
            if (name == "all")
            {
                return new HashSet<string>(NuGetSections.Everything, StringComparer.Ordinal);
            }

            if (NuGetSections.Everything.Contains(name))
            {
                result.Add(name);
            }
        }

        return result.Count == 0 ? null : result;
    }

    public static IReadOnlySet<string> DefaultSections(NuGetSymbolKind kind) => kind switch
    {
        NuGetSymbolKind.MethodGroup => new HashSet<string>([NuGetSections.Sig, NuGetSections.Doc, NuGetSections.Rel], StringComparer.Ordinal),
        NuGetSymbolKind.Enum => new HashSet<string>([NuGetSections.Data, NuGetSections.Doc], StringComparer.Ordinal),
        _ => new HashSet<string>([NuGetSections.Sig, NuGetSections.Methods, NuGetSections.Props, NuGetSections.Doc, NuGetSections.Rel], StringComparer.Ordinal)
    };

    /// <summary>A symbol id is exactly 12 lowercase hex characters.</summary>
    public static bool IsSid(string identifier) =>
        identifier.Length == SidLength && identifier.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
}
