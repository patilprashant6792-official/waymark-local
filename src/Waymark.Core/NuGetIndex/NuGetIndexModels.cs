namespace Waymark.Core.NuGetIndex;

public enum NuGetIndexState
{
    Building,
    Ready,
    Failed
}

public enum NuGetSymbolKind
{
    Class,
    Interface,
    Enum,
    Struct,
    Delegate,
    MethodGroup
}

/// <summary>Package id, version and target framework are compared ignoring case.</summary>
public sealed record NuGetPackageKey(string PackageId, string Version, string TargetFramework);

public sealed record NuGetPackageIndex(
    long Id,
    NuGetPackageKey Key,
    NuGetIndexState State,
    int SchemaVersion,
    int SymbolCount,
    int TypeCount,
    DateTimeOffset StartedUtc,
    DateTimeOffset? IndexedUtc,
    string? Error);

/// <summary>
/// One type or method group to store. Every section is pre-rendered text (empty when it has nothing to say).
/// <paramref name="DeclaringType"/> is set for a method group only.
/// </summary>
public sealed record NuGetSymbolEntry(
    string Sid,
    NuGetSymbolKind Kind,
    string Name,
    string FullName,
    string Namespace,
    string? DeclaringType,
    string Declaration,
    string Card,
    double Prominence,
    string Tokens,
    string Summary,
    string Related,
    string SignatureSection,
    string MethodsSection,
    string PropertiesSection,
    string DataSection,
    string DocSection);

/// <summary>
/// A candidate lookup inside one ready package. <see cref="Tokens"/> are alphanumeric words, each matched as a prefix and all
/// required; no tokens means browse mode, ordered by prominence. The store returns candidates; the caller re-ranks them.
/// </summary>
public sealed record NuGetSymbolQuery(
    long PackageRowId,
    IReadOnlyList<string> Tokens,
    NuGetSymbolKind? Kind,
    string? Namespace,
    string? DeclaringType,
    int Limit);

/// <param name="Relevance">Higher is better (negated bm25); 0 in browse mode.</param>
/// <param name="Sections">Section name to text, only the sections that were asked for.</param>
public sealed record NuGetSymbolHit(
    string Sid,
    NuGetSymbolKind Kind,
    string FullName,
    string Namespace,
    string Card,
    double Prominence,
    double Relevance,
    IReadOnlyDictionary<string, string> Sections);

/// <param name="TotalMatches">All matches in the package, not only the returned candidates.</param>
public sealed record NuGetSearchResult(IReadOnlyList<NuGetSymbolHit> Hits, int TotalMatches);

public sealed record NuGetNamespaceCount(string Namespace, int TypeCount);

public sealed record NuGetPackageOutline(NuGetPackageIndex Package, IReadOnlyList<NuGetNamespaceCount> Namespaces);

/// <param name="ResolvedVersion">The concrete version the payload was read from.</param>
public sealed record NuGetMetadataEntry(string CacheKey, string ResolvedVersion, string Payload, DateTimeOffset StoredUtc);
