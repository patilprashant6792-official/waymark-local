using System.Runtime.InteropServices;

namespace Waymark.Modules.NuGet.Loading;

/// <summary>
/// Assemblies of the installed .NET runtime and ASP.NET Core shared framework. MetadataLoadContext needs them to resolve
/// framework types in signatures; without the ASP.NET Core ones every type that touches IEndpointRouteBuilder,
/// IServiceCollection and similar loses all its members.
/// </summary>
internal static class FrameworkAssemblyPaths
{
    private static readonly Lazy<IReadOnlyList<string>> Cached = new(Discover);

    public static IReadOnlyList<string> All => Cached.Value;

    private static IReadOnlyList<string> Discover()
    {
        var paths = new List<string>();
        try
        {
            var runtimeDirectory = RuntimeEnvironment.GetRuntimeDirectory();
            paths.AddRange(Directory.GetFiles(runtimeDirectory, "*.dll"));
            paths.AddRange(FindAspNetCoreAssemblies(runtimeDirectory));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a host without the shared frameworks still loads packages that do not touch them.
        }

        return paths;
    }

    private static IEnumerable<string> FindAspNetCoreAssemblies(string runtimeDirectory)
    {
        var trimmed = runtimeDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var sharedRoot = Directory.GetParent(trimmed)?.Parent?.FullName;
        if (sharedRoot is null)
        {
            return [];
        }

        var aspNetRoot = Path.Combine(sharedRoot, "Microsoft.AspNetCore.App");
        if (!Directory.Exists(aspNetRoot))
        {
            return [];
        }

        var newest = Directory.GetDirectories(aspNetRoot)
            .Select(folder => (Folder: folder, Version: ParseVersion(Path.GetFileName(folder))))
            .Where(entry => entry.Version is not null)
            .OrderByDescending(entry => entry.Version)
            .Select(entry => entry.Folder)
            .FirstOrDefault();

        return newest is null ? [] : Directory.GetFiles(newest, "*.dll");
    }

    private static Version? ParseVersion(string folderName) =>
        Version.TryParse(folderName.Split('-', 2)[0], out var version) ? version : null;
}
