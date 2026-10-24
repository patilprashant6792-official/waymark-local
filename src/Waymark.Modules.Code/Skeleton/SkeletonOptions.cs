using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Waymark.Modules.Code.Skeleton;

/// <summary>Limits for the workspace overview tools: folder tree, changed files and folder listing.</summary>
/// <param name="OverviewBudgetChars">Size of the tree when code_skeleton is called without a depth; folders open breadth-first until it is used.</param>
/// <param name="MaxChildrenShown">Entries of one folder shown at once: the viewed folder is paged by it, and the overview leaves a bigger folder closed.</param>
/// <param name="MaxDepth">Largest depth a call may ask for.</param>
/// <param name="ScanBudgetSeconds">Time the folder scan may take before it stops and the result is marked partial.</param>
/// <param name="MaxProjectsInline">Up to this many projects under the viewed folder, the overview lists each project's packages; above it, one summary.</param>
/// <param name="SolutionInlineChars">The overview shows a solution file's text only up to this size; a bigger one is named with its size.</param>
/// <param name="LineCountReadBudgetBytes">Bytes one tree may read to count the lines of the files it shows; past it, only sizes are shown.</param>
public sealed record SkeletonOptions(
    int MaxTreeEntries,
    int CollapseThreshold,
    int LargeFileBytes,
    int MaxLineCountBytes,
    int MaxScanEntries,
    int MaxListedFiles,
    int MaxSolutionChars,
    int DefaultPageSize,
    int MaxPageSize,
    int OverviewBudgetChars,
    int MaxChildrenShown,
    int MaxDepth,
    int ScanBudgetSeconds,
    int MaxProjectsInline,
    int SolutionInlineChars,
    int LineCountReadBudgetBytes)
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
    public const int DefaultOverviewBudgetChars = 12_000;
    public const int DefaultMaxChildrenShown = 100;
    public const int DefaultMaxDepth = 64;
    public const int DefaultScanBudgetSeconds = 20;
    public const int DefaultMaxProjectsInline = 25;
    public const int DefaultSolutionInlineChars = 2_000;
    public const int DefaultLineCountReadBudgetBytes = 32 * 1024 * 1024;

    public static SkeletonOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var options = new SkeletonOptions(
            ReadPositive(configuration, nameof(MaxTreeEntries), DefaultMaxTreeEntries),
            ReadPositive(configuration, nameof(CollapseThreshold), DefaultCollapseThreshold),
            ReadPositive(configuration, nameof(LargeFileBytes), DefaultLargeFileBytes),
            ReadPositive(configuration, nameof(MaxLineCountBytes), DefaultMaxLineCountBytes),
            ReadPositive(configuration, nameof(MaxScanEntries), DefaultMaxScanEntries),
            ReadPositive(configuration, nameof(MaxListedFiles), DefaultMaxListedFiles),
            ReadPositive(configuration, nameof(MaxSolutionChars), DefaultMaxSolutionChars),
            ReadPositive(configuration, nameof(DefaultPageSize), DefaultDefaultPageSize),
            ReadPositive(configuration, nameof(MaxPageSize), DefaultMaxPageSize),
            ReadPositive(configuration, nameof(OverviewBudgetChars), DefaultOverviewBudgetChars),
            ReadPositive(configuration, nameof(MaxChildrenShown), DefaultMaxChildrenShown),
            ReadPositive(configuration, nameof(MaxDepth), DefaultMaxDepth),
            ReadPositive(configuration, nameof(ScanBudgetSeconds), DefaultScanBudgetSeconds),
            ReadPositive(configuration, nameof(MaxProjectsInline), DefaultMaxProjectsInline),
            ReadPositive(configuration, nameof(SolutionInlineChars), DefaultSolutionInlineChars),
            ReadPositive(configuration, nameof(LineCountReadBudgetBytes), DefaultLineCountReadBudgetBytes));

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
