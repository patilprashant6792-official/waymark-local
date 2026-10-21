using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Waymark.Modules.Code.Search;

/// <summary>Limits for code_search, code_callers and code_types.</summary>
public sealed record CodeSearchOptions(
    int MaxCandidates,
    int MaxCallSites,
    int DefaultPageSize,
    int MaxPageSize,
    int MaxQueryLength,
    int MaxTypeRelations = CodeSearchOptions.DefaultMaxTypeRelations)
{
    public const int DefaultMaxCandidates = 20_000;
    public const int DefaultMaxCallSites = 5_000;
    public const int DefaultDefaultPageSize = 20;
    public const int DefaultMaxPageSize = 200;
    public const int DefaultMaxQueryLength = 100;
    public const int DefaultMaxTypeRelations = 50_000;

    public static CodeSearchOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var options = new CodeSearchOptions(
            ReadPositive(configuration, "MaxCandidates", DefaultMaxCandidates),
            ReadPositive(configuration, "MaxCallSites", DefaultMaxCallSites),
            ReadPositive(configuration, "DefaultPageSize", DefaultDefaultPageSize),
            ReadPositive(configuration, "MaxPageSize", DefaultMaxPageSize),
            ReadPositive(configuration, "MaxQueryLength", DefaultMaxQueryLength),
            ReadPositive(configuration, "MaxTypeRelations", DefaultMaxTypeRelations));

        return options.DefaultPageSize <= options.MaxPageSize
            ? options
            : throw new InvalidOperationException("Waymark:Code:Search:DefaultPageSize must not exceed Waymark:Code:Search:MaxPageSize.");
    }

    private static int ReadPositive(IConfiguration configuration, string key, int fallback)
    {
        var name = $"Waymark:Code:Search:{key}";
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
