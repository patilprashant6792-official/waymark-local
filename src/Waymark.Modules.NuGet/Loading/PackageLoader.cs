using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Waymark.Core.NuGetIndex;

namespace Waymark.Modules.NuGet.Loading;

/// <summary>Validates a request, answers it from the metadata cache when it can and otherwise runs the load pipeline.</summary>
internal sealed class PackageLoader : IPackageLoader, IDisposable
{
    private static readonly JsonSerializerOptions PayloadOptions = new() { Converters = { new JsonStringEnumConverter() } };

    private readonly NuGetLoadOptions _options;
    private readonly INuGetMetadataStore _store;
    private readonly PackageLoadPipeline _pipeline;
    private readonly TimeProvider _time;
    private readonly ILogger<PackageLoader> _logger;
    private readonly SemaphoreSlim _gate;

    public PackageLoader(
        NuGetLoadOptions options,
        INuGetMetadataStore store,
        PackageLoadPipeline pipeline,
        TimeProvider time,
        ILogger<PackageLoader> logger)
    {
        _options = options;
        _store = store;
        _pipeline = pipeline;
        _time = time;
        _logger = logger;
        _gate = new SemaphoreSlim(options.MaxConcurrentLoads, options.MaxConcurrentLoads);
        LoadSession.RemoveStale(options.WorkDirectory, TimeSpan.FromHours(options.StaleWorkHours), logger);
    }

    public async Task<ReflectedPackage> LoadAsync(PackageLoadRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var packageId = NuGetInputRules.RequirePackageId(request.PackageId, nameof(request.PackageId));
        var pinned = NuGetInputRules.ParseVersion(request.Version);
        var framework = NuGetInputRules.NormalizeFramework(request.TargetFramework, _options.DefaultTargetFramework);
        var extras = NuGetInputRules.NormalizeExtraIds(request.AdditionalPackageIds);

        var pinnedText = pinned?.ToNormalizedString();
        var key = MetadataCacheKey.Build(packageId, pinnedText, framework, extras);

        // A forced refresh of a pinned version cannot know whether that version is also 'latest', so it drops the alias
        // instead of overwriting it with possibly older data; the next unpinned call resolves again and fills both keys.
        if (request.ForceRefresh && pinned is not null)
        {
            await _store.RemoveAsync(MetadataCacheKey.Build(packageId, null, framework, []), cancellationToken).ConfigureAwait(false);
        }

        if (!request.ForceRefresh && await TryReadAsync(key, cancellationToken).ConfigureAwait(false) is { } cached)
        {
            return cached;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Another caller may have finished the same package while this one waited.
            if (!request.ForceRefresh && await TryReadAsync(key, cancellationToken).ConfigureAwait(false) is { } finished)
            {
                return finished;
            }

            var timer = Stopwatch.StartNew();
            var loaded = await _pipeline
                .RunAsync(packageId, pinned, framework, request.IncludePrerelease, extras, cancellationToken)
                .ConfigureAwait(false);
            await StoreAsync(packageId, pinned is null, framework, extras, key, loaded, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Loaded {PackageId}@{Version}: {Types} types in {Elapsed} ms",
                packageId, loaded.ResolvedVersion, loaded.Types.Count, timer.ElapsedMilliseconds);
            return loaded;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _gate.Dispose();
    }

    private async Task<ReflectedPackage?> TryReadAsync(string key, CancellationToken cancellationToken)
    {
        var entry = await _store.GetAsync(key, cancellationToken).ConfigureAwait(false);
        if (entry is null)
        {
            return null;
        }

        try
        {
            var package = JsonSerializer.Deserialize<ReflectedPackage>(entry.Payload, PayloadOptions);
            if (package is { Types: not null, Documentation: not null } && !string.IsNullOrEmpty(package.ResolvedVersion))
            {
                return package;
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Cached package data for {Key} is unreadable and will be loaded again", key);
        }

        await _store.RemoveAsync(key, cancellationToken).ConfigureAwait(false);
        return null;
    }

    private async Task StoreAsync(
        string packageId,
        bool unpinned,
        string framework,
        IReadOnlyList<string> extras,
        string requestKey,
        ReflectedPackage package,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(package, PayloadOptions);
        var now = _time.GetUtcNow();
        var resolvedKey = MetadataCacheKey.Build(packageId, package.ResolvedVersion, framework, extras);

        await _store.SetAsync(resolvedKey, package.ResolvedVersion, payload, now, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(requestKey, resolvedKey, StringComparison.Ordinal) && extras.Count == 0 && unpinned)
        {
            // The 'latest' alias only ever holds results from the package's own dependencies, never a widened resolver set.
            await _store.SetAsync(requestKey, package.ResolvedVersion, payload, now, cancellationToken).ConfigureAwait(false);
        }
    }
}
