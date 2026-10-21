namespace Waymark.Core.NuGetIndex;

/// <summary>
/// A look-up in one indexed package for the admin UI. <see cref="Text"/> is free text, tokenised exactly as the nuget_find tool does;
/// no text means browse mode, most prominent first. <see cref="DeclaringType"/> limits the result to the method groups of one type.
/// </summary>
public sealed record NuGetExploreQuery(
    NuGetPackageKey Key,
    string? Text,
    NuGetSymbolKind? Kind,
    string? Namespace,
    string? DeclaringType,
    int Limit);

public sealed record NuGetSymbolSummary(string Sid, NuGetSymbolKind Kind, string FullName, string Namespace, string Card);

/// <param name="Total">All matches in the package, not only the returned items.</param>
public sealed record NuGetExploreResult(NuGetPackageIndex Package, IReadOnlyList<NuGetSymbolSummary> Items, int Total);

public sealed record NuGetDocumentation(
    string Summary,
    string Returns,
    IReadOnlyList<KeyValuePair<string, string>> Parameters,
    IReadOnlyList<KeyValuePair<string, string>> Exceptions);

public sealed record NuGetRelatedSymbol(string Label, string Sid, string FullName);

/// <summary>
/// Everything the index knows about one type or method group. The line lists are the pre-rendered sections, one declaration per line.
/// <see cref="MemberFilter"/> is the declaring-type value that lists the method groups of a type; null for a method group.
/// </summary>
public sealed record NuGetSymbolDetail(
    NuGetPackageIndex Package,
    string Sid,
    NuGetSymbolKind Kind,
    string FullName,
    string Namespace,
    string Card,
    IReadOnlyList<string> Constructors,
    IReadOnlyList<string> Methods,
    IReadOnlyList<string> Properties,
    IReadOnlyList<string> Fields,
    NuGetDocumentation Documentation,
    IReadOnlyList<NuGetRelatedSymbol> Related,
    string? MemberFilter,
    NuGetRelatedSymbol? Owner);

/// <summary>Reads the symbol index for people. Works on what is stored: it never downloads or indexes anything.</summary>
public interface INuGetExplorer
{
    /// <summary>Null when the package is not indexed or not ready.</summary>
    Task<NuGetExploreResult?> SearchAsync(NuGetExploreQuery query, CancellationToken ct);

    /// <param name="identifier">A symbol id, a full name, a simple name or the trailing part of a full name.</param>
    /// <returns>Null when the package is not ready or nothing matches.</returns>
    Task<NuGetSymbolDetail?> GetSymbolAsync(NuGetPackageKey key, string identifier, CancellationToken ct);
}
