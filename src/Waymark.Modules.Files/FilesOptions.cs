using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Waymark.Modules.Files;

/// <summary>Limits for the file-changing tools.</summary>
public sealed record FilesOptions(
    int MaxWriteBytes,
    int MaxBatchEntries,
    int MaxPatchesPerCall,
    int MaxFolderEntries,
    int MaxCopySourceBytes,
    int MaxTransformsPerCopy,
    int MaxCopyFilesPerCall)
{
    public const int DefaultMaxWriteBytes = 2_000_000;
    public const int DefaultMaxBatchEntries = 50;
    public const int DefaultMaxPatchesPerCall = 200;
    public const int DefaultMaxFolderEntries = 20_000;
    public const int DefaultMaxCopySourceBytes = 10_000_000;
    public const int DefaultMaxTransformsPerCopy = 50;
    public const int DefaultMaxCopyFilesPerCall = 500;

    public static FilesOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new FilesOptions(
            ReadPositive(configuration, "MaxWriteBytes", DefaultMaxWriteBytes),
            ReadPositive(configuration, "MaxBatchEntries", DefaultMaxBatchEntries),
            ReadPositive(configuration, "MaxPatchesPerCall", DefaultMaxPatchesPerCall),
            ReadPositive(configuration, "MaxFolderEntries", DefaultMaxFolderEntries),
            ReadPositive(configuration, "MaxCopySourceBytes", DefaultMaxCopySourceBytes),
            ReadPositive(configuration, "MaxTransformsPerCopy", DefaultMaxTransformsPerCopy),
            ReadPositive(configuration, "MaxCopyFilesPerCall", DefaultMaxCopyFilesPerCall));
    }

    private static int ReadPositive(IConfiguration configuration, string key, int fallback)
    {
        var name = "Waymark:Files:" + key;
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
