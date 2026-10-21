using Waymark.Core.NuGetIndex;

namespace Waymark.Host.NuGetAdmin;

/// <summary>What the explorer page needs from the NuGet index: the namespace map, searching, and one symbol in full. Read only.</summary>
public sealed class NuGetExplorerService(INuGetIndexStore store, INuGetExplorer explorer)
{
    /// <summary>Label shown for types that have no namespace; the explorer accepts it as a namespace filter.</summary>
    public const string GlobalNamespace = "(global)";

    public Task<NuGetPackageOutline?> GetOutlineAsync(NuGetPackageKey key, CancellationToken ct) => store.GetOutlineAsync(key, ct);

    public Task<NuGetExploreResult?> SearchAsync(NuGetExploreQuery query, CancellationToken ct) => explorer.SearchAsync(query, ct);

    public Task<NuGetSymbolDetail?> GetSymbolAsync(NuGetPackageKey key, string identifier, CancellationToken ct) =>
        explorer.GetSymbolAsync(key, identifier, ct);
}
