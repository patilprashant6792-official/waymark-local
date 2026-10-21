namespace Waymark.Modules.NuGet.Indexing;

/// <summary>The four nuget_* lookups. Every method returns markdown that is already bounded in size.</summary>
public interface INuGetSymbolService
{
    Task<string> PackagesAsync(string query, int? take, bool includePrerelease, CancellationToken cancellationToken);

    Task<string> OutlineAsync(PackageIndexRequest package, string? namespacePrefix, bool refresh, CancellationToken cancellationToken);

    Task<string> FindAsync(
        PackageIndexRequest package, string? query, string? kind, string? namespaceName, string? declaringType, int? limit, CancellationToken cancellationToken);

    Task<string> ExpandAsync(
        PackageIndexRequest package, IReadOnlyList<string> identifiers, string? sections, CancellationToken cancellationToken);
}
