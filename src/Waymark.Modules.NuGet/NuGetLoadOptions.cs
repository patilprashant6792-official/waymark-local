using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Waymark.Modules.NuGet;

/// <summary>Feed, folders and limits for loading a NuGet package and reflecting over its assemblies.</summary>
public sealed record NuGetLoadOptions(
    Uri FeedUrl,
    string GlobalPackagesDirectory,
    string WorkDirectory,
    string DefaultTargetFramework,
    int VersionLookupTimeoutSeconds,
    int DownloadTimeoutSeconds,
    long MaxPackageBytes,
    long MaxExtractedBytes,
    int MaxPackageFiles,
    int MaxDependencies,
    long MaxDocumentationBytes,
    int MaxConcurrentLoads,
    int StaleWorkHours)
{
    public const string DefaultFeedUrl = "https://api.nuget.org/v3/index.json";
    public const string DefaultFramework = "net10.0";
    public const int DefaultVersionLookupTimeoutSeconds = 30;
    public const int DefaultDownloadTimeoutSeconds = 120;
    public const long DefaultMaxPackageBytes = 250_000_000;
    public const long DefaultMaxExtractedBytes = 600_000_000;
    public const int DefaultMaxPackageFiles = 20_000;
    public const int DefaultMaxDependencies = 64;
    public const long DefaultMaxDocumentationBytes = 20_000_000;
    public const int DefaultMaxConcurrentLoads = 1;
    public const int DefaultStaleWorkHours = 24;

    private const string Prefix = "Waymark:NuGet:Load:";

    public static NuGetLoadOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var feed = ReadText(configuration, "FeedUrl", DefaultFeedUrl);
        if (!Uri.TryCreate(feed, UriKind.Absolute, out var feedUrl) || feedUrl.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException($"{Prefix}FeedUrl must be an absolute https URL.");
        }

        var framework = ReadText(configuration, "DefaultTargetFramework", DefaultFramework);
        return new NuGetLoadOptions(
            feedUrl,
            ReadPath(configuration, "GlobalPackagesDirectory", DefaultGlobalPackagesDirectory()),
            ReadPath(configuration, "WorkDirectory", Path.Combine(Path.GetTempPath(), "Waymark", "nuget-work")),
            framework,
            ReadInt(configuration, "VersionLookupTimeoutSeconds", DefaultVersionLookupTimeoutSeconds),
            ReadInt(configuration, "DownloadTimeoutSeconds", DefaultDownloadTimeoutSeconds),
            ReadPositive(configuration, "MaxPackageBytes", DefaultMaxPackageBytes),
            ReadPositive(configuration, "MaxExtractedBytes", DefaultMaxExtractedBytes),
            ReadInt(configuration, "MaxPackageFiles", DefaultMaxPackageFiles),
            ReadInt(configuration, "MaxDependencies", DefaultMaxDependencies),
            ReadPositive(configuration, "MaxDocumentationBytes", DefaultMaxDocumentationBytes),
            ReadInt(configuration, "MaxConcurrentLoads", DefaultMaxConcurrentLoads),
            ReadInt(configuration, "StaleWorkHours", DefaultStaleWorkHours));
    }

    private static string DefaultGlobalPackagesDirectory()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return fromEnvironment;
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
    }

    private static string ReadText(IConfiguration configuration, string key, string fallback)
    {
        var value = configuration[Prefix + key];
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static string ReadPath(IConfiguration configuration, string key, string fallback)
    {
        var value = ReadText(configuration, key, fallback);
        if (!Path.IsPathFullyQualified(value))
        {
            throw new InvalidOperationException($"{Prefix}{key} must be an absolute path.");
        }

        return Path.GetFullPath(value);
    }

    private static long ReadPositive(IConfiguration configuration, string key, long fallback)
    {
        var name = Prefix + key;
        var value = configuration[name];
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 1)
        {
            throw new InvalidOperationException($"{name} must be a positive whole number.");
        }

        return number;
    }

    private static int ReadInt(IConfiguration configuration, string key, int fallback)
    {
        var number = ReadPositive(configuration, key, fallback);
        if (number > int.MaxValue)
        {
            throw new InvalidOperationException($"{Prefix}{key} is too large.");
        }

        return (int)number;
    }
}
