using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Waymark.Modules.NuGet.Indexing;

/// <summary>Limits for the NuGet symbol index and the size of every nuget_* answer. Each limit is a token-budget control.</summary>
public sealed record NuGetIndexOptions(
    int DefaultFindResults,
    int MaxFindResults,
    int CandidateMultiplier,
    int MaxExpandSymbols,
    int MaxOutlineNamespaces,
    int MaxPackageResults,
    int MaxRelatedPerSymbol,
    int MaxFindOutputChars,
    int MaxExpandOutputChars,
    int MaxOutlineOutputChars,
    int MaxPackagesOutputChars,
    int MaxLinesPerSection,
    int MaxOverloadsPerExpand,
    int MaxCardChars,
    int MaxTokenFieldChars,
    int WriteBatchSize,
    int BuildStaleMinutes,
    int LatestVersionMinutes)
{
    /// <summary>Bump to force a rebuild of every indexed package after a change in what is stored.</summary>
    public const int SchemaVersion = 1;

    private const string Prefix = "Waymark:NuGet:Index:";

    public static NuGetIndexOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var options = new NuGetIndexOptions(
            Read(configuration, "DefaultFindResults", 10),
            Read(configuration, "MaxFindResults", 25),
            Read(configuration, "CandidateMultiplier", 3),
            Read(configuration, "MaxExpandSymbols", 5),
            Read(configuration, "MaxOutlineNamespaces", 25),
            Read(configuration, "MaxPackageResults", 8),
            Read(configuration, "MaxRelatedPerSymbol", 12),
            Read(configuration, "MaxFindOutputChars", 6000),
            Read(configuration, "MaxExpandOutputChars", 9000),
            Read(configuration, "MaxOutlineOutputChars", 2000),
            Read(configuration, "MaxPackagesOutputChars", 2500),
            Read(configuration, "MaxLinesPerSection", 60),
            Read(configuration, "MaxOverloadsPerExpand", 12),
            Read(configuration, "MaxCardChars", 170),
            Read(configuration, "MaxTokenFieldChars", 1200),
            Read(configuration, "WriteBatchSize", 500),
            Read(configuration, "BuildStaleMinutes", 10),
            Read(configuration, "LatestVersionMinutes", 60));

        return options.DefaultFindResults <= options.MaxFindResults
            ? options
            : throw new InvalidOperationException(Prefix + "DefaultFindResults must not exceed " + Prefix + "MaxFindResults.");
    }

    private static int Read(IConfiguration configuration, string key, int fallback)
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
