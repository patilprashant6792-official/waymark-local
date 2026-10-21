using Waymark.Core.NuGetIndex;
using Waymark.Modules.NuGet.Indexing;

namespace Waymark.Modules.NuGet;

/// <summary>Rebuilds one package from the feed for the admin UI: the indexer is asked to refresh, which skips the reflection cache too.</summary>
internal sealed class NuGetReloader(IPackageIndexer indexer) : INuGetReloader
{
    public async Task ReloadAsync(NuGetPackageKey key, IReadOnlyList<string> extraPackageIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(extraPackageIds);
        await indexer
            .EnsureIndexedAsync(new PackageIndexRequest(key.PackageId, key.Version, key.TargetFramework, IncludePrerelease: false, extraPackageIds), refresh: true, ct)
            .ConfigureAwait(false);
    }
}
