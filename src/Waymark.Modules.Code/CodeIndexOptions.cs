using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Waymark.Modules.Code;

/// <summary>Limits for parsing C# files into the code index and for keeping the index fresh.</summary>
public sealed record CodeIndexOptions(
    int MaxFileBytes,
    int MaxMembersPerFile,
    int MaxCallSitesPerFile,
    int WatcherDebounceMs,
    int MaxFilesPerWorkspace,
    int MaxPendingFiles,
    int ProgressEveryFiles)
{
    public const int DefaultMaxFileBytes = 2_000_000;
    public const int DefaultMaxMembersPerFile = 20_000;
    public const int DefaultMaxCallSitesPerFile = 20_000;
    public const int DefaultWatcherDebounceMs = 300;
    public const int DefaultMaxFilesPerWorkspace = 50_000;
    public const int DefaultMaxPendingFiles = 2_000;
    public const int DefaultProgressEveryFiles = 50;

    public static CodeIndexOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new CodeIndexOptions(
            ReadPositive(configuration, "MaxFileBytes", DefaultMaxFileBytes),
            ReadPositive(configuration, "MaxMembersPerFile", DefaultMaxMembersPerFile),
            ReadPositive(configuration, "MaxCallSitesPerFile", DefaultMaxCallSitesPerFile),
            ReadPositive(configuration, "WatcherDebounceMs", DefaultWatcherDebounceMs),
            ReadPositive(configuration, "MaxFilesPerWorkspace", DefaultMaxFilesPerWorkspace),
            ReadPositive(configuration, "MaxPendingFiles", DefaultMaxPendingFiles),
            ReadPositive(configuration, "ProgressEveryFiles", DefaultProgressEveryFiles));
    }

    private static int ReadPositive(IConfiguration configuration, string key, int fallback)
    {
        var name = $"Waymark:Code:{key}";
        var value = configuration[name];
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 1)
        {
            throw new InvalidOperationException($"{name} must be a positive whole number.");
        }

        return number;
    }
}
