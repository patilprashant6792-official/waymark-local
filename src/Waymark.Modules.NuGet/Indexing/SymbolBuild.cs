using Waymark.Core.NuGetIndex;

namespace Waymark.Modules.NuGet.Indexing;

/// <summary>What the factory produces for one package: the symbols to store and the namespace map for the outline.</summary>
internal sealed record SymbolBuild(IReadOnlyList<NuGetSymbolEntry> Symbols, IReadOnlyDictionary<string, int> TypesPerNamespace);
