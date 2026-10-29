using System.Globalization;
using Microsoft.Extensions.Configuration;
using Waymark.Core.Code;

namespace Waymark.Modules.Code;

/// <summary>Limits for parsing source files into the code index and for keeping the index fresh.</summary>
/// <param name="IndexParallelism">Files read and parsed at the same time. 0 means one less than the number of processors, at least 1.</param>
/// <param name="WriteBatchFiles">Most files stored in one database transaction.</param>
/// <param name="WriteBatchMs">Longest a parsed file waits for its batch to fill before the batch is stored anyway.</param>
public sealed record CodeIndexOptions(
    int MaxFileBytes,
    int MaxMembersPerFile,
    int MaxCallSitesPerFile,
    int WatcherDebounceMs,
    int MaxFilesPerWorkspace,
    int MaxPendingFiles,
    int ProgressEveryFiles,
    int IndexParallelism = 0,
    int WriteBatchFiles = CodeIndexOptions.DefaultWriteBatchFiles,
    int WriteBatchMs = CodeIndexOptions.DefaultWriteBatchMs)
{
    /// <summary>The limits every language indexer keeps to.</summary>
    public CodeIndexLimits Limits => new(MaxFileBytes, MaxMembersPerFile, MaxCallSitesPerFile);

    /// <summary>The number of files indexed at the same time, leaving one processor for the rest of the computer.</summary>
    public int EffectiveParallelism => IndexParallelism > 0 ? IndexParallelism : Math.Max(1, Environment.ProcessorCount - 1);

    public const int DefaultMaxFileBytes = 2_000_000;
    public const int DefaultMaxMembersPerFile = 20_000;
    public const int DefaultMaxCallSitesPerFile = 20_000;
    public const int DefaultWatcherDebounceMs = 300;
    public const int DefaultMaxFilesPerWorkspace = 50_000;
    public const int DefaultMaxPendingFiles = 2_000;
    public const int DefaultProgressEveryFiles = 50;
    public const int DefaultWriteBatchFiles = 200;
    public const int DefaultWriteBatchMs = 250;

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
            ReadPositive(configuration, "ProgressEveryFiles", DefaultProgressEveryFiles),
            ReadPositive(configuration, "IndexParallelism", 0),
            ReadPositive(configuration, "WriteBatchFiles", DefaultWriteBatchFiles),
            ReadPositive(configuration, "WriteBatchMs", DefaultWriteBatchMs));
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