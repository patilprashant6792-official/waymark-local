using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Waymark.Core.Code;
using Waymark.Core.NuGetIndex;
using Waymark.Languages.Python;
using Waymark.Modules.NuGet.Indexing;
using Waymark.Modules.NuGet.Loading;

namespace Waymark.Modules.NuGet.Pypi;

/// <summary>
/// Builds the symbol index of a PyPI release on first use, into the same tables as the NuGet index. The release is downloaded, its Python text is
/// unpacked and read with the Python indexer (syntax only, nothing is imported or run), and the symbols are stored. Builds run one at a time.
/// </summary>
internal sealed class PypiPackageIndexer : IPackageIndexer, IDisposable
{
    /// <summary>Kept apart from the NuGet schema version: a change to what a Python index stores rebuilds only the Python packages.</summary>
    private const int SchemaVersion = 1_001;

    private static readonly TimeSpan TakeoverPollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan TakeoverPollLimit = TimeSpan.FromSeconds(60);

    private readonly PypiClient _client;
    private readonly PypiPackageLoader _loader;
    private readonly PypiSymbolFactory _factory;
    private readonly PythonIndexer _python;
    private readonly INuGetIndexStore _store;
    private readonly NuGetIndexOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<PypiPackageIndexer> _logger;
    private readonly SemaphoreSlim _buildGate = new(1, 1);
    private readonly ConcurrentDictionary<string, LatestVersion> _latest = new(StringComparer.OrdinalIgnoreCase);

    public PypiPackageIndexer(
        PypiClient client,
        PypiPackageLoader loader,
        PythonIndexer python,
        INuGetIndexStore store,
        NuGetIndexOptions options,
        TimeProvider time,
        ILogger<PypiPackageIndexer> logger)
    {
        _client = client;
        _loader = loader;
        _python = python;
        _store = store;
        _options = options;
        _factory = new PypiSymbolFactory(options);
        _time = time;
        _logger = logger;
    }

    public async Task<NuGetPackageIndex> EnsureIndexedAsync(PackageIndexRequest request, bool refresh, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var name = PypiNames.Normalize(PypiNames.RequireProject(request.PackageId, nameof(request.PackageId)));
        var pinned = PypiNames.RequireVersion(request.Version);

        PypiProject? project = null;
        var version = pinned;
        if (version is null)
        {
            (version, project) = await ResolveLatestAsync(name, refresh, cancellationToken).ConfigureAwait(false);
        }

        var key = new NuGetPackageKey(name, version, PackageEcosystems.PyPITarget);
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

            project ??= await FetchAsync(name, version, cancellationToken).ConfigureAwait(false);
            return await BuildAsync(key, project, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _buildGate.Release();
        }
    }

    public void Dispose() => _buildGate.Dispose();

    private async Task<(string Version, PypiProject? Project)> ResolveLatestAsync(string name, bool refresh, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        if (!refresh && _latest.TryGetValue(name, out var cached) && cached.ExpiresUtc > now)
        {
            return (cached.Version, null);
        }

        var project = await FetchAsync(name, null, ct).ConfigureAwait(false);
        _latest[name] = new LatestVersion(project.Version, now.AddMinutes(_options.LatestVersionMinutes));
        return (project.Version, project);
    }

    private async Task<PypiProject> FetchAsync(string name, string? version, CancellationToken ct)
    {
        try
        {
            return await _client.GetAsync(name, version, ct).ConfigureAwait(false)
                ?? throw new NuGetLoadException(
                    version is null
                        ? $"There is no project '{name}' on the package index."
                        : $"The project '{name}' has no version '{version}' on the package index.",
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

    private async Task<NuGetPackageIndex> BuildAsync(NuGetPackageKey requested, PypiProject project, CancellationToken ct)
    {
        var key = requested with { Version = project.Version };
        var rowId = await _store
            .TryBeginBuildAsync(key, SchemaVersion, _time.GetUtcNow(), TimeSpan.FromMinutes(_options.BuildStaleMinutes), ct)
            .ConfigureAwait(false);
        if (rowId is null)
        {
            return await WaitForOtherBuildAsync(key, ct).ConfigureAwait(false);
        }

        LoadedPypiPackage? loaded = null;
        try
        {
            loaded = await _loader.LoadAsync(project, ct).ConfigureAwait(false);
            var indexed = new List<CodeFileIndex>(loaded.Files.Count);
            foreach (var source in loaded.Files)
            {
                ct.ThrowIfCancellationRequested();
                var result = await _python.IndexFileAsync(source.AbsolutePath, source.ModulePath, null, ct).ConfigureAwait(false);
                if (result.Outcome == SyntaxIndexOutcome.Unavailable)
                {
                    throw new NuGetLoadException("The Python parser did not load on this computer, so a Python package cannot be read.", key.PackageId, key.Version);
                }

                if (result.Outcome == SyntaxIndexOutcome.Indexed && result.File is not null)
                {
                    indexed.Add(result.File);
                }
            }

            var build = _factory.Build(key, indexed);
            if (build.Symbols.Count == 0)
            {
                throw new NuGetLoadException(
                    $"'{key.PackageId}' {key.Version} exposes no public Python names (every module is private, or the release has no Python source).",
                    key.PackageId,
                    key.Version);
            }

            foreach (var batch in build.Symbols.Chunk(_options.WriteBatchSize))
            {
                await _store.AddSymbolsAsync(rowId.Value, batch, ct).ConfigureAwait(false);
            }

            await _store.CompleteBuildAsync(rowId.Value, build.TypesPerNamespace, _time.GetUtcNow(), ct).ConfigureAwait(false);
            _logger.LogInformation(
                "Indexed {Package}@{Version} (PyPI): {Symbols} symbols in {Modules} modules, types from {Types}",
                key.PackageId,
                key.Version,
                build.Symbols.Count,
                build.TypesPerNamespace.Count,
                loaded.TypesFrom ?? "the package itself");
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
            _logger.LogError(ex, "Could not mark Python package index build {RowId} as failed", rowId);
        }
    }

    private sealed record LatestVersion(string Version, DateTimeOffset ExpiresUtc);
}

/// <summary>Sends a package lookup to the NuGet indexer or the PyPI indexer, by the ecosystem the request names.</summary>
internal sealed class EcosystemPackageIndexer(PackageIndexer nuget, PypiPackageIndexer pypi) : IPackageIndexer
{
    public Task<NuGetPackageIndex> EnsureIndexedAsync(PackageIndexRequest request, bool refresh, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return string.Equals(request.Ecosystem, PackageEcosystems.PyPI, StringComparison.OrdinalIgnoreCase)
            ? pypi.EnsureIndexedAsync(request, refresh, cancellationToken)
            : nuget.EnsureIndexedAsync(request, refresh, cancellationToken);
    }
}
