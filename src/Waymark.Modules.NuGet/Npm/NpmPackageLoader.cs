using Microsoft.Extensions.Logging;
using Waymark.Modules.NuGet.Loading;
using Waymark.Modules.NuGet.Pypi;

namespace Waymark.Modules.NuGet.Npm;

/// <summary>A package unpacked into a work folder, which is removed when the object is disposed.</summary>
/// <param name="Entries">The module paths a caller reaches first (the types entry files), used to rank their symbols higher.</param>
/// <param name="TypesFrom">The @types package that supplied the declarations, such as <c>@types/react 18.3.3</c>; null when the package carries its own.</param>
/// <param name="FromSource">No declarations exist anywhere, so the shape was read from the package's JavaScript.</param>
internal sealed class LoadedNpmPackage(string folder, IReadOnlyList<PackageSource> files, IReadOnlySet<string> entries, string? typesFrom, bool fromSource) : IDisposable
{
    public IReadOnlyList<PackageSource> Files { get; } = files;

    public IReadOnlySet<string> Entries { get; } = entries;

    public string? TypesFrom { get; } = typesFrom;

    public bool FromSource { get; } = fromSource;

    public void Dispose()
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file that is still open is removed with the next clean-up of the work folder.
        }
    }
}

/// <summary>
/// Fetches the tarball of one version and unpacks its declaration files. When the package declares none, the DefinitelyTyped package of the same name
/// (<c>@types/NAME</c>) supplies them; when that does not exist either, the package's own JavaScript is read, so a plain package still has a shape.
/// </summary>
internal sealed class NpmPackageLoader(NpmRegistryClient client, NpmOptions options, ILogger<NpmPackageLoader> logger)
{
    /// <exception cref="NuGetLoadException">The tarball cannot be downloaded, is too big, does not match its hash or cannot be unpacked.</exception>
    public async Task<LoadedNpmPackage> LoadAsync(NpmVersion version, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(version);
        Directory.CreateDirectory(options.WorkDirectory);
        RemoveStale();
        var work = Path.Combine(options.WorkDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var tarball = Path.Combine(work, "package.tgz");
            await DownloadAsync(version, tarball, ct).ConfigureAwait(false);
            var ownFolder = Path.Combine(work, "own");
            var own = Extract(version, tarball, ownFolder, includeScripts: false, ct);
            var entries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in TypesEntryResolver.Entries(own.PackageJson))
            {
                entries.Add(entry);
            }

            var sources = new List<PackageSource>();
            string? typesFrom = null;
            var fromSource = false;
            if (own.Files.Count > 0)
            {
                sources.AddRange(own.Files.Select(f => new PackageSource(Path.Combine(ownFolder, f.Replace('/', Path.DirectorySeparatorChar)), f, true)));
            }
            else
            {
                var types = await TryLoadTypesPackageAsync(version.Name, Path.Combine(work, "types"), ct).ConfigureAwait(false);
                if (types is { } found)
                {
                    typesFrom = found.Label;
                    entries.Clear();
                    foreach (var entry in found.Entries)
                    {
                        entries.Add(entry);
                    }

                    sources.AddRange(found.Files.Select(f => new PackageSource(Path.Combine(found.Folder, f.Replace('/', Path.DirectorySeparatorChar)), f, true)));
                }
                else
                {
                    var scripts = Extract(version, tarball, Path.Combine(work, "source"), includeScripts: true, ct);
                    fromSource = true;
                    sources.AddRange(scripts.Files.Select(f => new PackageSource(Path.Combine(work, "source", f.Replace('/', Path.DirectorySeparatorChar)), f, false)));
                }
            }

            logger.LogDebug("Unpacked {Name} {Version}: {Count} files, types from {Types}", version.Name, version.Version, sources.Count, typesFrom ?? (fromSource ? "none (source only)" : "the package"));
            return new LoadedNpmPackage(work, sources, entries, typesFrom, fromSource);
        }
        catch
        {
            TryDelete(work);
            throw;
        }
    }

    private async Task DownloadAsync(NpmVersion version, string destination, CancellationToken ct)
    {
        try
        {
            await client.DownloadAsync(version, destination, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidDataException or HttpRequestException or TimeoutException)
        {
            throw new PackageDownloadException($"Could not download '{version.Name}' {version.Version}: {ex.Message}", version.Name, version.Version, ex);
        }
    }

    private TarballContents Extract(NpmVersion version, string tarball, string folder, bool includeScripts, CancellationToken ct)
    {
        try
        {
            return TarballReader.Extract(tarball, folder, options, includeScripts, ct);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            throw new NuGetLoadException($"Could not unpack '{version.Name}' {version.Version}: {ex.Message}", ex, version.Name, version.Version);
        }
    }

    private async Task<(IReadOnlyList<string> Files, string Folder, IReadOnlyList<string> Entries, string Label)?> TryLoadTypesPackageAsync(string name, string folder, CancellationToken ct)
    {
        var typesName = NpmNames.TypesPackageOf(name);
        if (!NpmNames.IsPackage(typesName))
        {
            return null;
        }

        NpmVersion? types;
        try
        {
            types = await client.GetAsync(typesName, null, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or InvalidOperationException)
        {
            logger.LogDebug(ex, "Lookup of {Types} failed", typesName);
            return null;
        }

        if (types is null)
        {
            return null;
        }

        try
        {
            var tarball = Path.Combine(Path.GetDirectoryName(folder) ?? options.WorkDirectory, "types.tgz");
            await DownloadAsync(types, tarball, ct).ConfigureAwait(false);
            var contents = Extract(types, tarball, folder, includeScripts: false, ct);
            return contents.Files.Count == 0
                ? null
                : (contents.Files, folder, TypesEntryResolver.Entries(contents.PackageJson), $"{types.Name} {types.Version}");
        }
        catch (NuGetLoadException ex)
        {
            logger.LogDebug(ex, "The package {Types} could not be read", typesName);
            return null;
        }
    }

    private void RemoveStale()
    {
        try
        {
            foreach (var folder in Directory.EnumerateDirectories(options.WorkDirectory))
            {
                if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(folder) > TimeSpan.FromHours(6))
                {
                    TryDelete(folder);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "The work folder {Folder} could not be tidied", options.WorkDirectory);
        }
    }

    private static void TryDelete(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for the next clean-up.
        }
    }
}
