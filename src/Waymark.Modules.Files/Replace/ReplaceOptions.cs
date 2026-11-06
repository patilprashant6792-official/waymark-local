using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Waymark.Modules.Files.Replace;

/// <summary>Limits for file_replace. Every value can be changed under Waymark:Files:Replace.</summary>
public sealed record ReplaceOptions(
    int MaxFiles,
    int MaxMatches,
    int MaxFindChars,
    int MaxWithChars,
    int PlanMinutes,
    int MaxOpenPlans,
    int ScanBudgetSeconds,
    int MaxPreviewLinesPerFile)
{
    private const string Prefix = "Waymark:Files:Replace:";

    public static ReplaceOptions Defaults { get; } = new(
        MaxFiles: 200,
        MaxMatches: 5_000,
        MaxFindChars: 200,
        MaxWithChars: 1_000,
        PlanMinutes: 10,
        MaxOpenPlans: 16,
        ScanBudgetSeconds: 20,
        MaxPreviewLinesPerFile: 20);

    public static ReplaceOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var defaults = Defaults;
        return new ReplaceOptions(
            ReadPositive(configuration, nameof(MaxFiles), defaults.MaxFiles),
            ReadPositive(configuration, nameof(MaxMatches), defaults.MaxMatches),
            ReadPositive(configuration, nameof(MaxFindChars), defaults.MaxFindChars),
            ReadPositive(configuration, nameof(MaxWithChars), defaults.MaxWithChars),
            ReadPositive(configuration, nameof(PlanMinutes), defaults.PlanMinutes),
            ReadPositive(configuration, nameof(MaxOpenPlans), defaults.MaxOpenPlans),
            ReadPositive(configuration, nameof(ScanBudgetSeconds), defaults.ScanBudgetSeconds),
            ReadPositive(configuration, nameof(MaxPreviewLinesPerFile), defaults.MaxPreviewLinesPerFile));
    }

    private static int ReadPositive(IConfiguration configuration, string key, int fallback)
    {
        var name = Prefix + key;
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
