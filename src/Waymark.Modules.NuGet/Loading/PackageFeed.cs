using Microsoft.Extensions.Logging;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Packaging;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace Waymark.Modules.NuGet.Loading;

/// <summary>Talks to the configured NuGet feed: finds versions and puts a package's lib files and nuspec on disk.</summary>
internal sealed class PackageFeed : IDisposable
{
    private const int CopyBufferBytes = 81_920;

    private readonly NuGetLoadOptions _options;
    private readonly ILogger<PackageFeed> _logger;
    private readonly SourceRepository _repository;
    private readonly SourceCacheContext _cacheContext = new();

    public PackageFeed(NuGetLoadOptions options, ILogger<PackageFeed> logger)
    {
        _options = options;
        _logger = logger;
        _repository = Repository.Factory.GetCoreV3(new PackageSource(options.FeedUrl.AbsoluteUri));
    }

    public async Task<NuGetVersion> ResolveLatestAsync(string packageId, bool includePrerelease, CancellationToken cancellationToken)
    {
        var resource = await FindResourceAsync(cancellationToken).ConfigureAwait(false);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(_options.VersionLookupTimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        IEnumerable<NuGetVersion>? versions;
        try
        {
            versions = await resource.GetAllVersionsAsync(packageId, _cacheContext, NullLogger.Instance, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Timeout while fetching versions for package '{packageId}'.");
        }

        var latest = (versions ?? [])
            .Where(version => includePrerelease || !version.IsPrerelease)
            .OrderByDescending(version => version)
            .FirstOrDefault();

        return latest ?? throw new InvalidOperationException(
            $"No versions found for package '{packageId}'. Include prerelease: {includePrerelease}.");
    }

    /// <summary>Free-text package search on the feed. Returns at most <paramref name="take"/> hits, best first.</summary>
    public async Task<IReadOnlyList<PackageSearchHit>> SearchPackagesAsync(
        string query, int take, bool includePrerelease, CancellationToken cancellationToken)
    {
        var resource = await _repository.GetResourceAsync<PackageSearchResource>(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The configured NuGet feed does not support package search.");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(_options.VersionLookupTimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            var results = await resource
                .SearchAsync(query, new SearchFilter(includePrerelease), 0, take, NullLogger.Instance, linked.Token)
                .ConfigureAwait(false);
            return (results ?? [])
                .Select(r => new PackageSearchHit(r.Identity.Id, r.Identity.Version.ToNormalizedString(), r.DownloadCount, r.Description ?? string.Empty))
                .ToList();
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Timeout while searching packages for '{query}'.");
        }
    }

    /// <summary>Returns a folder holding the package: the shared packages folder when it has the package, otherwise a download into the session.</summary>
    public async Task<string> AcquireAsync(LoadSession session, string packageId, NuGetVersion version, CancellationToken cancellationToken)
    {
        var shared = FindShared(packageId, version);
        if (shared is not null)
        {
            _logger.LogDebug("Using shared package folder for {PackageId}@{Version}", packageId, version);
            return shared;
        }

        var target = session.NewPackageFolder(packageId, version);
        var resource = await FindResourceAsync(cancellationToken).ConfigureAwait(false);

        await using var nupkg = new FileStream(
            session.NewTempFilePath(), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, CopyBufferBytes,
            FileOptions.Asynchronous | FileOptions.DeleteOnClose);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(_options.DownloadTimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            var found = await resource.CopyNupkgToStreamAsync(packageId, version, nupkg, _cacheContext, NullLogger.Instance, linked.Token)
                .ConfigureAwait(false);
            if (!found)
            {
                throw new InvalidOperationException($"The feed has no package '{packageId}@{version}'.");
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Timeout while downloading package '{packageId}@{version}'.");
        }

        if (nupkg.Length > _options.MaxPackageBytes)
        {
            throw new NuGetLoadException(
                $"Package '{packageId}@{version}' is {nupkg.Length / 1_000_000} MB, above the limit of {_options.MaxPackageBytes / 1_000_000} MB.",
                packageId, version.ToString());
        }

        nupkg.Seek(0, SeekOrigin.Begin);
        await ExtractAsync(nupkg, target, packageId, version, cancellationToken).ConfigureAwait(false);
        return target;
    }

    private async Task<FindPackageByIdResource> FindResourceAsync(CancellationToken cancellationToken) =>
        await _repository.GetResourceAsync<FindPackageByIdResource>(cancellationToken).ConfigureAwait(false)
        ?? throw new InvalidOperationException("The configured NuGet feed does not offer package downloads.");


    public void Dispose()
    {
        _cacheContext.Dispose();
    }

    private string? FindShared(string packageId, NuGetVersion version)
    {
        var folder = Path.Combine(
            _options.GlobalPackagesDirectory,
            packageId.ToLowerInvariant(),
            version.ToNormalizedString().ToLowerInvariant());

        return Directory.Exists(Path.Combine(folder, "lib")) ? folder : null;
    }

    /// <summary>Extracts the nuspec and everything under lib/. Entries that would land outside the target folder abort the load.</summary>
    private async Task ExtractAsync(Stream nupkg, string target, string packageId, NuGetVersion version, CancellationToken cancellationToken)
    {
        using var reader = new PackageArchiveReader(nupkg);
        var wanted = reader.GetFiles()
            .Where(entry => entry.StartsWith("lib/", StringComparison.OrdinalIgnoreCase)
                            || (!entry.Contains('/') && entry.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (wanted.Count > _options.MaxPackageFiles)
        {
            throw new NuGetLoadException(
                $"Package '{packageId}@{version}' has {wanted.Count} files to read, above the limit of {_options.MaxPackageFiles}.",
                packageId, version.ToString());
        }

        var root = Path.GetFullPath(target) + Path.DirectorySeparatorChar;
        long total = 0;

        foreach (var entry in wanted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = Path.GetFullPath(Path.Combine(root, entry.Replace('/', Path.DirectorySeparatorChar)));
            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new NuGetLoadException(
                    $"Package '{packageId}@{version}' contains an unsafe file path and was not read.", packageId, version.ToString());
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await using var source = reader.GetStream(entry);
                await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferBytes, useAsync: true);
                total += await CopyBoundedAsync(source, output, _options.MaxExtractedBytes - total, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Could not extract {Entry} from {PackageId}@{Version}", entry, packageId, version);
            }
        }
    }

    private static async Task<long> CopyBoundedAsync(Stream source, Stream destination, long remaining, CancellationToken cancellationToken)
    {
        var buffer = new byte[CopyBufferBytes];
        long copied = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            copied += read;
            if (copied > remaining)
            {
                throw new NuGetLoadException("The package expands to more data than the configured limit allows.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return copied;
    }
}
