namespace Waymark.Modules.NuGet.Loading;

/// <summary>A package could not be loaded for a reason the caller can act on. The message is safe to show to the AI client.</summary>
public class NuGetLoadException : Exception
{
    public NuGetLoadException(string message, string? packageId = null, string? version = null)
        : base(message)
    {
        PackageId = packageId;
        Version = version;
    }

    public NuGetLoadException(string message, Exception innerException, string? packageId = null, string? version = null)
        : base(message, innerException)
    {
        PackageId = packageId;
        Version = version;
    }

    public string? PackageId { get; }

    public string? Version { get; }
}

public sealed class PackageDownloadException : NuGetLoadException
{
    public PackageDownloadException(string message, string packageId, string? version, Exception? innerException = null)
        : base(message, innerException ?? new InvalidOperationException(message), packageId, version)
    {
    }
}

/// <summary>
/// Reflection could not resolve assemblies that live in a transitive dependency the package does not list directly.
/// The caller retries with those package ids as additional package ids; the loader never walks the dependency tree on its own.
/// </summary>
public sealed class MissingSiblingAssembliesException : NuGetLoadException
{
    public MissingSiblingAssembliesException(string packageId, string? version, IReadOnlyList<string> missingAssemblyNames)
        : base(BuildMessage(packageId, missingAssemblyNames), packageId, version)
    {
        MissingAssemblyNames = missingAssemblyNames;
    }

    public IReadOnlyList<string> MissingAssemblyNames { get; }

    private static string BuildMessage(string packageId, IReadOnlyList<string> missingAssemblyNames)
    {
        var first = missingAssemblyNames.Count > 0 ? missingAssemblyNames[0] : "unknown";
        var plural = missingAssemblyNames.Count == 1 ? "y is" : "ies are";
        return $"Could not fully resolve '{packageId}': {missingAssemblyNames.Count} referenced assembl{plural} defined in a transitive " +
               $"dependency this package doesn't list directly: {string.Join(", ", missingAssemblyNames)}. " +
               "Retry with additionalPackageIds set to these package id(s) (for example the NuGet package that ships " +
               $"'{first}.dll') to pull them in as resolver assemblies.";
    }
}
