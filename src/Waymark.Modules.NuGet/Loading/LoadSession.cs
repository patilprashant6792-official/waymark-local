using Microsoft.Extensions.Logging;
using NuGet.Versioning;

namespace Waymark.Modules.NuGet.Loading;

/// <summary>
/// A private scratch folder for the packages downloaded during one load. Everything inside is deleted on dispose.
/// Packages found in the shared packages folder are never copied here and never deleted.
/// </summary>
internal sealed class LoadSession : IDisposable
{
    private readonly ILogger _logger;

    public LoadSession(string workDirectory, ILogger logger)
    {
        _logger = logger;
        Root = Path.Combine(Path.GetFullPath(workDirectory), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string NewPackageFolder(string packageId, NuGetVersion version)
    {
        var folder = Path.Combine(Root, "pkg", packageId, version.ToNormalizedString());
        Directory.CreateDirectory(folder);
        return folder;
    }

    public string NewTempFilePath() => Path.Combine(Root, Guid.NewGuid().ToString("N") + ".nupkg");

    public void Dispose()
    {
        DeleteFolder(Root, _logger);
    }

    /// <summary>Removes scratch folders left behind by a crashed run.</summary>
    public static void RemoveStale(string workDirectory, TimeSpan maxAge, ILogger logger)
    {
        try
        {
            if (!Directory.Exists(workDirectory))
            {
                return;
            }

            var cutoff = DateTime.UtcNow - maxAge;
            foreach (var folder in Directory.EnumerateDirectories(workDirectory))
            {
                if (Directory.GetLastWriteTimeUtc(folder) < cutoff)
                {
                    DeleteFolder(folder, logger);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not clean the NuGet work folder {Folder}", workDirectory);
        }
    }

    private static void DeleteFolder(string folder, ILogger logger)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not delete the NuGet work folder {Folder}", folder);
        }
    }
}
