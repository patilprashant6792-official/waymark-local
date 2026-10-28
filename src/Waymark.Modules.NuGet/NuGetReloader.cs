using Waymark.Core.NuGetIndex;
using Waymark.Modules.NuGet.Indexing;

namespace Waymark.Modules.NuGet;

/// <summary>Rebuilds one package from its feed or index for the admin UI: the indexer is asked to refresh, which skips the reflection cache too.</summary>
internal sealed class NuGetReloader(IPackageIndexer indexer) : INuGetReloader
{
    public async Task ReloadAsync(NuGetPackageKey key, IReadOnlyList<string> extraPackageIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(extraPackageIds);
        var request = new PackageIndexRequest(
            key.PackageId,
            key.Version,
            key.TargetFramework,
            IncludePrerelease: false,
            extraPackageIds,
            PackageEcosystems.Of(key));
        await indexer.EnsureIndexedAsync(request, refresh: true, ct).ConfigureAwait(false);
    }
}
