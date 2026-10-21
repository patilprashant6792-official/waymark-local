using Microsoft.Extensions.Logging;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Versioning;

namespace Waymark.Modules.NuGet.Loading;

/// <summary>A package the target framework's dependency group names. <paramref name="MinVersion"/> is null when the range has no lower bound.</summary>
internal sealed record PackageDependencyRef(string Id, NuGetVersion? MinVersion);

/// <summary>Reads an extracted package folder: which assemblies to reflect over and which packages it depends on.</summary>
internal static class PackageContents
{
    private static readonly string[] FallbackFolders =
    [
        "net10.0", "net9.0", "net8.0", "net6.0", "netstandard2.1", "netstandard2.0", "netstandard1.6"
    ];

    public static IReadOnlyList<string> FindAssemblies(string packageFolder, string targetFramework, ILogger logger)
    {
        var libFolder = Path.Combine(packageFolder, "lib");
        if (!Directory.Exists(libFolder))
        {
            logger.LogWarning("No lib folder in package folder {PackageFolder}", packageFolder);
            return [];
        }

        var target = NuGetFramework.ParseFolder(targetFramework);
        var candidates = new List<(NuGetFramework Framework, string Folder)>();
        foreach (var folder in Directory.EnumerateDirectories(libFolder))
        {
            var framework = NuGetFramework.ParseFolder(Path.GetFileName(folder));
            if (!framework.IsUnsupported)
            {
                candidates.Add((framework, folder));
            }
        }

        var nearest = new FrameworkReducer().GetNearest(target, candidates.Select(candidate => candidate.Framework));
        if (nearest is not null)
        {
            var match = candidates.First(candidate => candidate.Framework.Equals(nearest));
            var inNearest = TopLevelAssemblies(match.Folder);
            if (inNearest.Count > 0)
            {
                return inNearest;
            }
        }

        foreach (var name in FallbackFolders)
        {
            var inFallback = TopLevelAssemblies(Path.Combine(libFolder, name));
            if (inFallback.Count > 0)
            {
                return inFallback;
            }
        }

        logger.LogDebug("No matching framework folder in {LibFolder}; using every assembly under lib", libFolder);
        return Directory.GetFiles(libFolder, "*.dll", SearchOption.AllDirectories);
    }

    public static IReadOnlyList<PackageDependencyRef> ReadDependencies(string packageFolder, string targetFramework, ILogger logger)
    {
        var nuspec = Directory.GetFiles(packageFolder, "*.nuspec", SearchOption.TopDirectoryOnly).FirstOrDefault();
        if (nuspec is null)
        {
            logger.LogWarning("No nuspec in package folder {PackageFolder}", packageFolder);
            return [];
        }

        NuspecReader reader;
        using (var stream = File.OpenRead(nuspec))
        {
            reader = new NuspecReader(stream);
        }

        var groups = reader.GetDependencyGroups().ToList();
        var nearest = new FrameworkReducer().GetNearest(NuGetFramework.ParseFolder(targetFramework), groups.Select(group => group.TargetFramework));
        var chosen = nearest is null ? null : groups.FirstOrDefault(group => group.TargetFramework.Equals(nearest));
        if (chosen is null)
        {
            logger.LogDebug("No dependency group matches framework {Framework}", targetFramework);
            return [];
        }

        var result = new List<PackageDependencyRef>();
        foreach (var dependency in chosen.Packages)
        {
            try
            {
                result.Add(new PackageDependencyRef(NuGetInputRules.RequirePackageId(dependency.Id, nameof(dependency)), dependency.VersionRange?.MinVersion));
            }
            catch (ArgumentException ex)
            {
                logger.LogWarning(ex, "Skipping dependency with an unusable id in {PackageFolder}", packageFolder);
            }
        }

        return result;
    }

    private static List<string> TopLevelAssemblies(string folder) =>
        Directory.Exists(folder) ? Directory.GetFiles(folder, "*.dll", SearchOption.TopDirectoryOnly).ToList() : [];
}
