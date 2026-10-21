using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Waymark.Modules.Code.Skeleton;

/// <summary>Limits for the workspace overview tools: folder tree, changed files and folder listing.</summary>
public sealed record SkeletonOptions(
    int MaxTreeEntries,
    int CollapseThreshold,
    int LargeFileBytes,
    int MaxLineCountBytes,
    int MaxScanEntries,
    int MaxListedFiles,
    int MaxSolutionChars,
    int DefaultPageSize,
    int MaxPageSize)
{
    public const int DefaultMaxTreeEntries = 5_000;
    public const int DefaultCollapseThreshold = 50;
    public const int DefaultLargeFileBytes = 15 * 1024;
    public const int DefaultMaxLineCountBytes = 1_000_000;
    public const int DefaultMaxScanEntries = 100_000;
    public const int DefaultMaxListedFiles = 500;
    public const int DefaultMaxSolutionChars = 20_000;
    public const int DefaultDefaultPageSize = 50;
    public const int DefaultMaxPageSize = 200;

    public static SkeletonOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var options = new SkeletonOptions(
            ReadPositive(configuration, "MaxTreeEntries", DefaultMaxTreeEntries),
            ReadPositive(configuration, "CollapseThreshold", DefaultCollapseThreshold),
            ReadPositive(configuration, "LargeFileBytes", DefaultLargeFileBytes),
            ReadPositive(configuration, "MaxLineCountBytes", DefaultMaxLineCountBytes),
            ReadPositive(configuration, "MaxScanEntries", DefaultMaxScanEntries),
            ReadPositive(configuration, "MaxListedFiles", DefaultMaxListedFiles),
            ReadPositive(configuration, "MaxSolutionChars", DefaultMaxSolutionChars),
            ReadPositive(configuration, "DefaultPageSize", DefaultDefaultPageSize),
            ReadPositive(configuration, "MaxPageSize", DefaultMaxPageSize));

        return options.DefaultPageSize <= options.MaxPageSize
            ? options
            : throw new InvalidOperationException("Waymark:Code:Skeleton:DefaultPageSize must not exceed Waymark:Code:Skeleton:MaxPageSize.");
    }

    private static int ReadPositive(IConfiguration configuration, string key, int fallback)
    {
        var name = $"Waymark:Code:Skeleton:{key}";
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
