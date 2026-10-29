using Waymark.Modules.NuGet.Loading;

namespace Waymark.Modules.NuGet.Npm;

/// <summary>Searches the public npm registry with its own search endpoint.</summary>
internal sealed class NpmPackageSearch(NpmRegistryClient client)
{
    public Task<IReadOnlyList<PackageSearchHit>> SearchAsync(string query, int take, CancellationToken ct) => client.SearchAsync(query, take, ct);
}
