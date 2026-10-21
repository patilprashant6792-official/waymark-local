using Microsoft.Extensions.Logging;
using NuGet.Versioning;

namespace Waymark.Modules.NuGet.Loading;

/// <summary>The uncached work of a load: resolve the version, fetch the package and its direct dependencies, reflect, read the docs.</summary>
internal sealed class PackageLoadPipeline(
    NuGetLoadOptions options,
    PackageFeed feed,
    AssemblyReflector reflector,
    ILogger<PackageLoadPipeline> logger)
{
    /// <param name="requestedVersion">The pinned version, or null for the latest.</param>
    public async Task<ReflectedPackage> RunAsync(
        string packageId,
        NuGetVersion? requestedVersion,
        string targetFramework,
        bool includePrerelease,
        IReadOnlyList<string> extraIds,
        CancellationToken cancellationToken)
    {
        var version = requestedVersion ?? await ResolveLatestAsync(packageId, includePrerelease, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Loading {PackageId}@{Version} for {Framework}", packageId, version, targetFramework);

        using var session = new LoadSession(options.WorkDirectory, logger);
        var packageFolder = await AcquireMainAsync(session, packageId, version, cancellationToken).ConfigureAwait(false);

        var dependencyAssemblies = new List<string>();
        await AddDependencyAssembliesAsync(session, packageFolder, targetFramework, dependencyAssemblies, cancellationToken).ConfigureAwait(false);
        await AddExtraAssembliesAsync(session, extraIds, targetFramework, includePrerelease, dependencyAssemblies, cancellationToken).ConfigureAwait(false);

        var assemblies = PackageContents.FindAssemblies(packageFolder, targetFramework, logger);
        logger.LogInformation("{PackageId}@{Version}: {Assemblies} assemblies, {Dependencies} dependency assemblies",
            packageId, version, assemblies.Count, dependencyAssemblies.Count);

        var types = new List<ReflectedType>();
        var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var assembly in assemblies)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Sibling DLLs of the same package must be resolvable, or every signature that uses a type from one of them is dropped.
            var resolvers = assemblies.Where(other => other != assembly).Concat(dependencyAssemblies).ToList();
            try
            {
                var reflected = await Task.Run(() => reflector.Reflect(assembly, resolvers, cancellationToken), cancellationToken).ConfigureAwait(false);
                types.AddRange(reflected);
            }
            catch (MissingSiblingAssembliesException ex)
            {
                logger.LogWarning(ex, "{Assembly} has unresolved siblings: {Missing}", Path.GetFileName(assembly), string.Join(", ", ex.MissingAssemblyNames));
                missing.UnionWith(ex.MissingAssemblyNames);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not read the metadata of {Assembly}", Path.GetFileName(assembly));
            }
        }

        // Assemblies but no types and a named gap is a resolution failure, not an empty package: say what to add.
        if (assemblies.Count > 0 && types.Count == 0 && missing.Count > 0)
        {
            throw new MissingSiblingAssembliesException(packageId, version.ToNormalizedString(), missing.ToList());
        }

        return new ReflectedPackage(version.ToNormalizedString(), types, ReadDocumentation(assemblies));
    }

    private async Task<NuGetVersion> ResolveLatestAsync(string packageId, bool includePrerelease, CancellationToken cancellationToken)
    {
        try
        {
            return await feed.ResolveLatestAsync(packageId, includePrerelease, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not NuGetLoadException)
        {
            logger.LogError(ex, "Could not resolve the latest version of {PackageId}", packageId);
            throw new NuGetLoadException(ex.Message, ex, packageId, null);
        }
    }

    private async Task<string> AcquireMainAsync(LoadSession session, string packageId, NuGetVersion version, CancellationToken cancellationToken)
    {
        try
        {
            return await feed.AcquireAsync(session, packageId, version, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not NuGetLoadException)
        {
            logger.LogError(ex, "Could not download {PackageId}@{Version}", packageId, version);
            throw new PackageDownloadException(
                $"Failed to download '{packageId}@{version}'. Either this exact version was never published to the configured feed " +
                "(check the version list on nuget.org; a project can be pinned to a version that was skipped or never released), " +
                "or the feed is temporarily unreachable.",
                packageId, version.ToString(), ex);
        }
    }

    /// <summary>Direct dependencies of the target framework's group, one level only. A dependency that cannot be fetched is skipped.</summary>
    private async Task AddDependencyAssembliesAsync(
        LoadSession session, string packageFolder, string targetFramework, List<string> into, CancellationToken cancellationToken)
    {
        IReadOnlyList<PackageDependencyRef> dependencies;
        try
        {
            dependencies = PackageContents.ReadDependencies(packageFolder, targetFramework, logger);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not read the dependencies of the package in {PackageFolder}", packageFolder);
            return;
        }

        foreach (var dependency in dependencies.Take(options.MaxDependencies))
        {
            await TryAddAsync(session, dependency.Id, dependency.MinVersion, targetFramework, false, into, cancellationToken).ConfigureAwait(false);
        }

        if (dependencies.Count > options.MaxDependencies)
        {
            logger.LogWarning("Only the first {Max} of {Count} dependencies were fetched", options.MaxDependencies, dependencies.Count);
        }
    }

    /// <summary>Packages the caller named after a MissingSiblingAssembliesException. Never recursive: each one is a single package.</summary>
    private async Task AddExtraAssembliesAsync(
        LoadSession session, IReadOnlyList<string> extraIds, string targetFramework, bool includePrerelease, List<string> into, CancellationToken cancellationToken)
    {
        foreach (var extraId in extraIds)
        {
            await TryAddAsync(session, extraId, null, targetFramework, includePrerelease, into, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task TryAddAsync(
        LoadSession session,
        string packageId,
        NuGetVersion? minVersion,
        string targetFramework,
        bool includePrerelease,
        List<string> into,
        CancellationToken cancellationToken)
    {
        try
        {
            var version = minVersion ?? await feed.ResolveLatestAsync(packageId, includePrerelease, cancellationToken).ConfigureAwait(false);
            var folder = await feed.AcquireAsync(session, packageId, version, cancellationToken).ConfigureAwait(false);
            var found = PackageContents.FindAssemblies(folder, targetFramework, logger);
            into.AddRange(found);
            logger.LogDebug("Added {Count} assemblies from {PackageId}@{Version}", found.Count, packageId, version);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not add the assemblies of {PackageId}", packageId);
        }
    }

    /// <summary>The XML documentation file next to each reflected assembly, read now because a downloaded folder is deleted afterwards.</summary>
    private List<DocumentationFile> ReadDocumentation(IReadOnlyList<string> assemblies)
    {
        var result = new List<DocumentationFile>();
        long total = 0;
        foreach (var assembly in assemblies)
        {
            var xmlPath = Path.ChangeExtension(assembly, ".xml");
            try
            {
                var info = new FileInfo(xmlPath);
                if (!info.Exists)
                {
                    continue;
                }

                if (total + info.Length > options.MaxDocumentationBytes)
                {
                    logger.LogWarning("Documentation limit of {Limit} bytes reached; {File} and later files were not read", options.MaxDocumentationBytes, info.Name);
                    break;
                }

                result.Add(new DocumentationFile(info.Name, File.ReadAllText(xmlPath)));
                total += info.Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Could not read the documentation file {File}", Path.GetFileName(xmlPath));
            }
        }

        return result;
    }
}
