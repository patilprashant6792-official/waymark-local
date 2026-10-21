using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Waymark.Core.NuGetIndex;
using Waymark.Modules.NuGet.Loading;

namespace Waymark.Modules.NuGet.Indexing;

/// <summary>
/// Builds the symbol index of a package on first use. Builds run one at a time: reflection is heavy and a second
/// request for the same package should wait for the first instead of repeating it.
/// </summary>
internal sealed class PackageIndexer : IPackageIndexer, IDisposable
{
    private static readonly TimeSpan TakeoverPollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan TakeoverPollLimit = TimeSpan.FromSeconds(30);

    private readonly IPackageLoader _loader;
    private readonly PackageFeed _feed;
    private readonly INuGetIndexStore _store;
    private readonly NuGetIndexOptions _options;
    private readonly NuGetLoadOptions _loadOptions;
    private readonly SymbolDocumentFactory _factory;
    private readonly TimeProvider _time;
    private readonly ILogger<PackageIndexer> _logger;
    private readonly SemaphoreSlim _buildGate = new(1, 1);
    private readonly ConcurrentDictionary<string, LatestVersion> _latest = new(StringComparer.OrdinalIgnoreCase);

    public PackageIndexer(
        IPackageLoader loader,
        PackageFeed feed,
        INuGetIndexStore store,
        NuGetIndexOptions options,
        NuGetLoadOptions loadOptions,
        TimeProvider time,
        ILogger<PackageIndexer> logger)
    {
        _loader = loader;
        _feed = feed;
        _store = store;
        _options = options;
        _loadOptions = loadOptions;
        _factory = new SymbolDocumentFactory(options);
        _time = time;
        _logger = logger;
    }

    public async Task<NuGetPackageIndex> EnsureIndexedAsync(PackageIndexRequest request, bool refresh, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var packageId = NuGetInputRules.RequirePackageId(request.PackageId, nameof(request.PackageId));
        var pinned = NuGetInputRules.ParseVersion(request.Version)?.ToNormalizedString();
        var framework = NuGetInputRules.NormalizeFramework(request.TargetFramework, _loadOptions.DefaultTargetFramework);
        var extras = NuGetInputRules.NormalizeExtraIds(request.AdditionalPackageIds);

        var version = pinned ?? await ResolveLatestAsync(packageId, framework, request.IncludePrerelease, refresh, cancellationToken).ConfigureAwait(false);
        var key = new NuGetPackageKey(packageId, version, framework);

        if (!refresh && await TryGetUsableAsync(key, cancellationToken).ConfigureAwait(false) is { } existing)
        {
            return existing;
        }

        await _buildGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!refresh && await TryGetUsableAsync(key, cancellationToken).ConfigureAwait(false) is { } finished)
            {
                return finished;
            }

            return await BuildAsync(key, request.IncludePrerelease, extras, refresh, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _buildGate.Release();
        }
    }

    public void Dispose()
    {
        _buildGate.Dispose();
    }

    private async Task<string> ResolveLatestAsync(string packageId, string framework, bool includePrerelease, bool refresh, CancellationToken cancellationToken)
    {
        var cacheKey = $"{packageId}|{framework}|{includePrerelease}";
        var now = _time.GetUtcNow();
        if (!refresh && _latest.TryGetValue(cacheKey, out var cached) && cached.ExpiresUtc > now)
        {
            return cached.Version;
        }

        try
        {
            var latest = await _feed.ResolveLatestAsync(packageId, includePrerelease, cancellationToken).ConfigureAwait(false);
            var version = latest.ToNormalizedString();
            _latest[cacheKey] = new LatestVersion(version, now.AddMinutes(_options.LatestVersionMinutes));
            return version;
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or HttpRequestException)
        {
            throw new NuGetLoadException(ex.Message, ex, packageId);
        }
    }

    private async Task<NuGetPackageIndex?> TryGetUsableAsync(NuGetPackageKey key, CancellationToken cancellationToken)
    {
        var package = await _store.GetPackageAsync(key, cancellationToken).ConfigureAwait(false);
        return package is { State: NuGetIndexState.Ready, SymbolCount: > 0 } && package.SchemaVersion == NuGetIndexOptions.SchemaVersion
            ? package
            : null;
    }

    private async Task<NuGetPackageIndex> BuildAsync(
        NuGetPackageKey requested, bool includePrerelease, IReadOnlyList<string> extras, bool refresh, CancellationToken cancellationToken)
    {
        var loaded = await _loader
            .LoadAsync(new PackageLoadRequest(requested.PackageId, requested.Version, requested.TargetFramework, includePrerelease, refresh, extras), cancellationToken)
            .ConfigureAwait(false);

        var key = requested with { Version = loaded.ResolvedVersion };
        var started = _time.GetUtcNow();
        var rowId = await _store
            .TryBeginBuildAsync(key, NuGetIndexOptions.SchemaVersion, started, TimeSpan.FromMinutes(_options.BuildStaleMinutes), cancellationToken)
            .ConfigureAwait(false);

        if (rowId is null)
        {
            return await WaitForOtherBuildAsync(key, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var docs = XmlDocParser.Parse(loaded.Documentation, _logger);
            var build = _factory.Build(key, loaded.Types, docs);
            if (build.Symbols.Count == 0)
            {
                throw new NuGetLoadException(
                    $"'{key.PackageId}' {key.Version} exposes no public types for {key.TargetFramework}. Try another targetFramework, or additionalPackageIds if it is a meta-package.",
                    key.PackageId,
                    key.Version);
            }

            foreach (var batch in build.Symbols.Chunk(_options.WriteBatchSize))
            {
                await _store.AddSymbolsAsync(rowId.Value, batch, cancellationToken).ConfigureAwait(false);
            }

            await _store.CompleteBuildAsync(rowId.Value, build.TypesPerNamespace, _time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Indexed {PackageId}@{Version} ({Tfm}): {Symbols} symbols in {Namespaces} namespaces",
                key.PackageId, key.Version, key.TargetFramework, build.Symbols.Count, build.TypesPerNamespace.Count);
        }
        catch (Exception ex)
        {
            await FailQuietlyAsync(rowId.Value, ex).ConfigureAwait(false);
            throw;
        }

        return await TryGetUsableAsync(key, cancellationToken).ConfigureAwait(false)
            ?? throw new NuGetLoadException($"Indexing '{key.PackageId}' {key.Version} finished but the index is not readable.", key.PackageId, key.Version);
    }

    private async Task<NuGetPackageIndex> WaitForOtherBuildAsync(NuGetPackageKey key, CancellationToken cancellationToken)
    {
        var waited = TimeSpan.Zero;
        while (waited < TakeoverPollLimit)
        {
            await Task.Delay(TakeoverPollInterval, _time, cancellationToken).ConfigureAwait(false);
            waited += TakeoverPollInterval;
            if (await TryGetUsableAsync(key, cancellationToken).ConfigureAwait(false) is { } ready)
            {
                return ready;
            }
        }

        throw new NuGetLoadException(
            $"'{key.PackageId}' {key.Version} is still being indexed by another request. Try again in a minute.", key.PackageId, key.Version);
    }

    private async Task FailQuietlyAsync(long rowId, Exception cause)
    {
        try
        {
            await _store.FailBuildAsync(rowId, Truncate(cause.Message), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not mark NuGet index build {RowId} as failed", rowId);
        }
    }

    private static string Truncate(string message) => message.Length <= 500 ? message : message[..500];

    private sealed record LatestVersion(string Version, DateTimeOffset ExpiresUtc);
}
