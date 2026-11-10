using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Waymark.Modules.Code.Search;

/// <summary>Limits for code_search, code_callers and code_types.</summary>
/// <param name="MaxTerms">Most words a code_search query and its terms may have together.</param>
/// <param name="ContentCandidates">Most hits one full-text lookup returns before ranking.</param>
/// <param name="RrfK">Smoothing constant of the rank fusion; the larger, the flatter the difference between the first and the tenth place.</param>
/// <param name="CallerBoostTop">How many of the best candidates have their callers counted for the ranking boost.</param>
public sealed record CodeSearchOptions(
    int MaxCandidates,
    int MaxCallSites,
    int DefaultPageSize,
    int MaxPageSize,
    int MaxQueryLength,
    int MaxTypeRelations = CodeSearchOptions.DefaultMaxTypeRelations,
    int MaxTerms = CodeSearchOptions.DefaultMaxTerms,
    int ContentCandidates = CodeSearchOptions.DefaultContentCandidates,
    int RrfK = CodeSearchOptions.DefaultRrfK,
    int CallerBoostTop = CodeSearchOptions.DefaultCallerBoostTop)
{
    public const int DefaultMaxCandidates = 20_000;
    public const int DefaultMaxCallSites = 5_000;
    public const int DefaultDefaultPageSize = 20;
    public const int DefaultMaxPageSize = 200;
    public const int DefaultMaxQueryLength = 200;
    public const int DefaultMaxTypeRelations = 50_000;
    public const int DefaultMaxTerms = 16;
    public const int DefaultContentCandidates = 300;
    public const int DefaultRrfK = 60;
    public const int DefaultCallerBoostTop = 200;

    public static CodeSearchOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var options = new CodeSearchOptions(
            ReadPositive(configuration, "MaxCandidates", DefaultMaxCandidates),
            ReadPositive(configuration, "MaxCallSites", DefaultMaxCallSites),
            ReadPositive(configuration, "DefaultPageSize", DefaultDefaultPageSize),
            ReadPositive(configuration, "MaxPageSize", DefaultMaxPageSize),
            ReadPositive(configuration, "MaxQueryLength", DefaultMaxQueryLength),
            ReadPositive(configuration, "MaxTypeRelations", DefaultMaxTypeRelations),
            ReadPositive(configuration, "MaxTerms", DefaultMaxTerms),
            ReadPositive(configuration, "ContentCandidates", DefaultContentCandidates),
            ReadPositive(configuration, "RrfK", DefaultRrfK),
            ReadPositive(configuration, "CallerBoostTop", DefaultCallerBoostTop));

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
