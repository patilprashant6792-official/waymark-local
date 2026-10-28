using Microsoft.Extensions.Logging;
using Waymark.Modules.NuGet.Loading;

namespace Waymark.Modules.NuGet.Pypi;

/// <summary>One Python file of a package, ready to be read.</summary>
/// <param name="AbsolutePath">Where the file was unpacked.</param>
/// <param name="ModulePath">The path that names its module (<c>requests/api.py</c>), with a stub package's <c>requests-stubs/</c> folder renamed to <c>requests/</c>.</param>
/// <param name="IsStub">The file comes from a stub package or is a .pyi file.</param>
internal sealed record PackageSource(string AbsolutePath, string ModulePath, bool IsStub);

/// <summary>A release unpacked into a work folder, which is removed when the object is disposed.</summary>
internal sealed class LoadedPypiPackage(string folder, IReadOnlyList<PackageSource> files, string? typesFrom) : IDisposable
{
    public IReadOnlyList<PackageSource> Files { get; } = files;

    /// <summary>The stub package that supplied the types, such as <c>types-requests 2.32.0</c>; null when the package carries its own or has none.</summary>
    public string? TypesFrom { get; } = typesFrom;

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
/// Fetches the files of one release and unpacks their Python text. A wheel is preferred; a package that has none is read from its source archive,
/// which is only unpacked, never built. When the package has no types of its own, the stub package of the same project (<c>types-NAME</c> or
/// <c>NAME-stubs</c>) supplies them, in the order PEP 561 gives.
/// </summary>
internal sealed class PypiPackageLoader(PypiClient client, PypiOptions options, ILogger<PypiPackageLoader> logger)
{
    /// <exception cref="NuGetLoadException">The release has no file Waymark can read, or the file is too big or does not match its checksum.</exception>
    public async Task<LoadedPypiPackage> LoadAsync(PypiProject project, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        Directory.CreateDirectory(options.WorkDirectory);
        RemoveStale();
        var work = Path.Combine(options.WorkDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var own = await FetchAsync(project, Path.Combine(work, "own"), ct).ConfigureAwait(false);
            var ownRoot = Path.Combine(work, "own");
            var typed = own.HasPyTyped || own.Files.Any(f => f.EndsWith(".pyi", StringComparison.OrdinalIgnoreCase));

            var sources = new List<PackageSource>();
            string? typesFrom = null;
            if (!typed)
            {
                var stubs = await TryFetchStubsAsync(project, Path.Combine(work, "stubs"), ct).ConfigureAwait(false);
                if (stubs is { } found)
                {
                    typesFrom = found.Label;
                    foreach (var file in found.Contents.Files.Where(f => f.EndsWith(".pyi", StringComparison.OrdinalIgnoreCase)))
                    {
                        sources.Add(new PackageSource(Path.Combine(found.Root, file.Replace('/', Path.DirectorySeparatorChar)), RenameStubFolder(file), true));
                    }
                }
            }

            // For one module path a .pyi stands in for the .py beside it, and a stub package stands in for the package's own file.
            var covered = new HashSet<string>(sources.Select(s => WithoutExtension(s.ModulePath)), StringComparer.OrdinalIgnoreCase);
            foreach (var file in own.Files.Where(f => !f.EndsWith("py.typed", StringComparison.Ordinal)).OrderBy(f => f.EndsWith(".pyi", StringComparison.OrdinalIgnoreCase) ? 0 : 1))
            {
                if (covered.Add(WithoutExtension(file)))
                {
                    sources.Add(new PackageSource(
                        Path.Combine(ownRoot, file.Replace('/', Path.DirectorySeparatorChar)),
                        file,
                        file.EndsWith(".pyi", StringComparison.OrdinalIgnoreCase)));
                }
            }

            logger.LogDebug("Unpacked {Name} {Version}: {Count} Python files, types from {Types}", project.Name, project.Version, sources.Count, typesFrom ?? (typed ? "the package" : "none"));
            return new LoadedPypiPackage(work, sources, typesFrom);
        }
        catch
        {
            TryDelete(work);
            throw;
        }
    }

    private async Task<ArchiveContents> FetchAsync(PypiProject project, string folder, CancellationToken ct)
    {
        var file = ChooseFile(project)
            ?? throw new NuGetLoadException(
                $"'{project.Name}' {project.Version} has no wheel and no .tar.gz source archive on the index, so there is nothing Waymark can read.",
                project.Name,
                project.Version);
        Directory.CreateDirectory(folder);
        var archive = Path.Combine(folder, "..", "archive-" + Guid.NewGuid().ToString("N") + (file.IsWheel ? ".whl" : ".tar.gz"));
        archive = Path.GetFullPath(archive);
        try
        {
            try
            {
                await client.DownloadAsync(file, archive, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidDataException or HttpRequestException or TimeoutException)
            {
                throw new PackageDownloadException($"Could not download '{file.FileName}': {ex.Message}", project.Name, project.Version, ex);
            }

            try
            {
                return file.IsWheel
                    ? ArchiveReader.ExtractWheel(archive, folder, options, ct)
                    : ArchiveReader.ExtractSdist(archive, folder, options, ct);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                throw new NuGetLoadException($"Could not unpack '{file.FileName}': {ex.Message}", ex, project.Name, project.Version);
            }
        }
        finally
        {
            TryDeleteFile(archive);
        }
    }

    /// <summary>A wheel for any platform first (it is pure Python), else the smallest wheel; a .tar.gz source archive when there is no wheel.</summary>
    private static PypiFile? ChooseFile(PypiProject project)
    {
        var usable = project.Files.Where(f => !f.Yanked).ToList();
        var wheel = usable
            .Where(f => f.IsWheel)
            .OrderBy(f => f.FileName.EndsWith("-none-any.whl", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(f => f.Size)
            .FirstOrDefault();
        return wheel ?? usable.FirstOrDefault(f => f.IsSdist && f.FileName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<(ArchiveContents Contents, string Root, string Label)?> TryFetchStubsAsync(PypiProject project, string folder, CancellationToken ct)
    {
        var name = PypiNames.Normalize(project.Name);
        foreach (var candidate in new[] { $"types-{name}", $"{name}-stubs" })
        {
            PypiProject? stubs;
            try
            {
                stubs = await client.GetAsync(candidate, null, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TimeoutException or InvalidOperationException)
            {
                logger.LogDebug(ex, "Stub lookup for {Candidate} failed", candidate);
                continue;
            }

            if (stubs is null || !stubs.Files.Any(f => f.IsWheel && !f.Yanked))
            {
                continue;
            }

            try
            {
                var contents = await FetchAsync(stubs, folder, ct).ConfigureAwait(false);
                if (contents.Files.Any(f => f.EndsWith(".pyi", StringComparison.OrdinalIgnoreCase)))
                {
                    return (contents, folder, $"{stubs.Name} {stubs.Version}");
                }
            }
            catch (NuGetLoadException ex)
            {
                logger.LogDebug(ex, "Stub package {Candidate} could not be read", candidate);
            }
        }

        return null;
    }

    /// <summary><c>requests-stubs/api.pyi</c> is the module <c>requests/api.pyi</c>.</summary>
    private static string RenameStubFolder(string path)
    {
        var slash = path.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0)
        {
            return path;
        }

        var top = path[..slash];
        return top.EndsWith("-stubs", StringComparison.OrdinalIgnoreCase) ? top[..^"-stubs".Length] + path[slash..] : path;
    }

    private static string WithoutExtension(string path)
    {
        var dot = path.LastIndexOf('.');
        return dot > path.LastIndexOf('/') ? path[..dot] : path;
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

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for the next clean-up.
        }
    }
}
