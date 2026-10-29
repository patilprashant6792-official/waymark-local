using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Waymark.Core.Code;
using Waymark.Core.NuGetIndex;
using Waymark.Languages.TypeScript;
using Waymark.Modules.NuGet.Indexing;
using Waymark.Modules.NuGet.Loading;

namespace Waymark.Modules.NuGet.Npm;

/// <summary>
/// Builds the symbol index of an npm package version on first use, into the same tables as the NuGet index. The tarball is downloaded and checked against the
/// registry's integrity hash, its declaration files are unpacked and read with the TypeScript indexer (syntax only, nothing is installed, imported or run), and
/// the symbols are stored. Builds run one at a time.
/// </summary>
internal sealed class NpmPackageIndexer : IPackageIndexer, IDisposable
{
    /// <summary>Kept apart from the NuGet and PyPI schema versions: a change to what an npm index stores rebuilds only the npm packages.</summary>
    private const int SchemaVersion = 2_001;

    private static readonly TimeSpan TakeoverPollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan TakeoverPollLimit = TimeSpan.FromSeconds(60);

    private readonly NpmRegistryClient _client;
    private readonly NpmPackageLoader _loader;
    private readonly NpmSymbolFactory _factory;
    private readonly TypeScriptIndexer _indexer;
    private readonly INuGetIndexStore _store;
    private readonly NuGetIndexOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<NpmPackageIndexer> _logger;
    private readonly SemaphoreSlim _buildGate = new(1, 1);
    private readonly ConcurrentDictionary<string, LatestVersion> _latest = new(StringComparer.OrdinalIgnoreCase);

    public NpmPackageIndexer(
        NpmRegistryClient client,
        NpmPackageLoader loader,
        TypeScriptIndexer indexer,
        INuGetIndexStore store,
        NuGetIndexOptions options,
        TimeProvider time,
        ILogger<NpmPackageIndexer> logger)
    {
        _client = client;
        _loader = loader;
        _indexer = indexer;
        _store = store;
        _options = options;
        _factory = new NpmSymbolFactory(options);
        _time = time;
        _logger = logger;
    }

    public async Task<NuGetPackageIndex> EnsureIndexedAsync(PackageIndexRequest request, bool refresh, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var name = NpmNames.RequirePackage(request.PackageId, nameof(request.PackageId));
        var pinned = NpmNames.RequireVersion(request.Version);

        NpmVersion? resolved = null;
        var version = pinned;
        if (version is null)
        {
            (version, resolved) = await ResolveLatestAsync(name, refresh, cancellationToken).ConfigureAwait(false);
        }

        var key = new NuGetPackageKey(name, version, PackageEcosystems.NpmTarget);
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

            resolved ??= await FetchAsync(name, version, cancellationToken).ConfigureAwait(false);
            return await BuildAsync(key, resolved, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _buildGate.Release();
        }
    }

    public void Dispose() => _buildGate.Dispose();

    private async Task<(string Version, NpmVersion? Resolved)> ResolveLatestAsync(string name, bool refresh, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        if (!refresh && _latest.TryGetValue(name, out var cached) && cached.ExpiresUtc > now)
        {
            return (cached.Version, null);
        }

        var latest = await FetchAsync(name, null, ct).ConfigureAwait(false);
        _latest[name] = new LatestVersion(latest.Version, now.AddMinutes(_options.LatestVersionMinutes));
        return (latest.Version, latest);
    }

    private async Task<NpmVersion> FetchAsync(string name, string? version, CancellationToken ct)
    {
        try
        {
            return await _client.GetAsync(name, version, ct).ConfigureAwait(false)
                ?? throw new NuGetLoadException(
                    version is null
                        ? $"There is no package '{name}' on the npm registry."
                        : $"The package '{name}' has no version '{version}' on the npm registry.",
                    name,
                    version);
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException)
        {
            throw new NuGetLoadException(ex.Message, ex, name, version);
        }
    }

    private async Task<NuGetPackageIndex?> TryGetUsableAsync(NuGetPackageKey key, CancellationToken ct)
    {
        var package = await _store.GetPackageAsync(key, ct).ConfigureAwait(false);
        return package is { State: NuGetIndexState.Ready, SymbolCount: > 0 } && package.SchemaVersion == SchemaVersion ? package : null;
    }

    private async Task<NuGetPackageIndex> BuildAsync(NuGetPackageKey requested, NpmVersion resolved, CancellationToken ct)
    {
        var key = requested with { Version = resolved.Version };
        var rowId = await _store
            .TryBeginBuildAsync(key, SchemaVersion, _time.GetUtcNow(), TimeSpan.FromMinutes(_options.BuildStaleMinutes), ct)
            .ConfigureAwait(false);
        if (rowId is null)
        {
            return await WaitForOtherBuildAsync(key, ct).ConfigureAwait(false);
        }

        LoadedNpmPackage? loaded = null;
        try
        {
            loaded = await _loader.LoadAsync(resolved, ct).ConfigureAwait(false);
            var indexed = new List<CodeFileIndex>(loaded.Files.Count);
            foreach (var source in loaded.Files)
            {
                ct.ThrowIfCancellationRequested();
                var result = await _indexer.IndexFileAsync(source.AbsolutePath, source.ModulePath, null, ct).ConfigureAwait(false);
                if (result.Outcome == SyntaxIndexOutcome.Unavailable)
                {
                    throw new NuGetLoadException("The TypeScript parser did not load on this computer, so an npm package cannot be read.", key.PackageId, key.Version);
                }

                if (result.Outcome == SyntaxIndexOutcome.Indexed && result.File is not null)
                {
                    indexed.Add(result.File);
                }
            }

            var build = _factory.Build(key, key.PackageId, indexed, loaded.Entries);
            if (build.Symbols.Count == 0)
            {
                throw new NuGetLoadException(
                    $"'{key.PackageId}' {key.Version} exposes no public declarations (it ships no types, no @types package exists, and its JavaScript has no exported names).",
                    key.PackageId,
                    key.Version);
            }

            foreach (var batch in build.Symbols.Chunk(_options.WriteBatchSize))
            {
                await _store.AddSymbolsAsync(rowId.Value, batch, ct).ConfigureAwait(false);
            }

            await _store.CompleteBuildAsync(rowId.Value, build.TypesPerNamespace, _time.GetUtcNow(), ct).ConfigureAwait(false);
            _logger.LogInformation(
                "Indexed {Package}@{Version} (npm): {Symbols} symbols in {Modules} modules, types from {Types}",
                key.PackageId,
                key.Version,
                build.Symbols.Count,
                build.TypesPerNamespace.Count,
                loaded.TypesFrom ?? (loaded.FromSource ? "its JavaScript source" : "the package itself"));
        }
        catch (Exception ex)
        {
            await FailQuietlyAsync(rowId.Value, ex).ConfigureAwait(false);
            throw;
        }
        finally
        {
            loaded?.Dispose();
        }

        return await TryGetUsableAsync(key, ct).ConfigureAwait(false)
            ?? throw new NuGetLoadException($"Indexing '{key.PackageId}' {key.Version} finished but the index is not readable.", key.PackageId, key.Version);
    }

    private async Task<NuGetPackageIndex> WaitForOtherBuildAsync(NuGetPackageKey key, CancellationToken ct)
    {
        var waited = TimeSpan.Zero;
        while (waited < TakeoverPollLimit)
        {
            await Task.Delay(TakeoverPollInterval, _time, ct).ConfigureAwait(false);
            waited += TakeoverPollInterval;
            if (await TryGetUsableAsync(key, ct).ConfigureAwait(false) is { } ready)
            {
                return ready;
            }
        }

        throw new NuGetLoadException($"'{key.PackageId}' {key.Version} is still being indexed by another request. Try again in a minute.", key.PackageId, key.Version);
    }

    private async Task FailQuietlyAsync(long rowId, Exception cause)
    {
        try
        {
            var message = cause.Message.Length <= 500 ? cause.Message : cause.Message[..500];
            await _store.FailBuildAsync(rowId, message, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not mark npm package index build {RowId} as failed", rowId);
        }
    }

    private sealed record LatestVersion(string Version, DateTimeOffset ExpiresUtc);
}
