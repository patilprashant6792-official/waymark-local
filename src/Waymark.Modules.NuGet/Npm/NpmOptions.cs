using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Waymark.Modules.NuGet.Npm;

/// <summary>The registry, the folders and the limits for reading an npm package. Set under <c>Waymark:Npm:*</c>.</summary>
/// <param name="RegistryUrl">The base address of the public registry. A private registry is out of scope: its tokens live in .npmrc, which Waymark never reads.</param>
/// <param name="WorkDirectory">Where a package is unpacked while it is indexed; it is removed afterwards.</param>
/// <param name="MaxArchiveBytes">The largest tarball that is downloaded.</param>
/// <param name="MaxExtractedBytes">The most declaration text that is unpacked from one tarball.</param>
/// <param name="MaxFileBytes">A declaration or script file bigger than this is skipped.</param>
public sealed record NpmOptions(
    Uri RegistryUrl,
    string WorkDirectory,
    int LookupTimeoutSeconds,
    int DownloadTimeoutSeconds,
    long MaxArchiveBytes,
    long MaxExtractedBytes,
    int MaxFiles,
    long MaxFileBytes)
{
    public const string DefaultRegistryUrl = "https://registry.npmjs.org/";

    private const string Prefix = "Waymark:Npm:";

    public static NpmOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var registry = ReadText(configuration, "RegistryUrl", DefaultRegistryUrl);
        if (!Uri.TryCreate(registry.EndsWith('/') ? registry : registry + "/", UriKind.Absolute, out var registryUrl) || registryUrl.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException($"{Prefix}RegistryUrl must be an absolute https URL.");
        }

        var work = ReadText(configuration, "WorkDirectory", Path.Combine(Path.GetTempPath(), "Waymark", "npm-work"));
        if (!Path.IsPathFullyQualified(work))
        {
            throw new InvalidOperationException($"{Prefix}WorkDirectory must be an absolute path.");
        }

        return new NpmOptions(
            registryUrl,
            Path.GetFullPath(work),
            ReadInt(configuration, "LookupTimeoutSeconds", 30),
            ReadInt(configuration, "DownloadTimeoutSeconds", 180),
            ReadLong(configuration, "MaxArchiveBytes", 100_000_000),
            ReadLong(configuration, "MaxExtractedBytes", 150_000_000),
            ReadInt(configuration, "MaxFiles", 20_000),
            ReadLong(configuration, "MaxFileBytes", 2_000_000));
    }

    private static string ReadText(IConfiguration configuration, string key, string fallback)
    {
        var value = configuration[Prefix + key];
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static long ReadLong(IConfiguration configuration, string key, long fallback)
    {
        var name = Prefix + key;
        var value = configuration[name];
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number >= 1
            ? number
            : throw new InvalidOperationException($"{name} must be a positive whole number.");
    }

    private static int ReadInt(IConfiguration configuration, string key, int fallback)
    {
        var number = ReadLong(configuration, key, fallback);
        return number <= int.MaxValue ? (int)number : throw new InvalidOperationException($"{Prefix}{key} is too large.");
    }
}
