using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Waymark.Modules.Code.Content;

/// <summary>Limits for code_grep. Every value can be changed under Waymark:Code:Grep.</summary>
public sealed record ContentOptions(
    int MaxFileBytes,
    int MaxFilesPerWorkspace,
    int MaxQueryLength,
    int MaxLinesPerFile,
    int MaxMatchingFiles,
    int MaxDisplayedLineChars,
    int PageSize,
    int MaxPageSize,
    int ScanBudgetSeconds,
    int MinifiedMinBytes,
    int MinifiedAverageLineChars,
    IReadOnlyList<string> VendoredFolders)
{
    private const string Prefix = "Waymark:Code:Grep:";
    private const string NoFolders = "none";

    public static ContentOptions Defaults { get; } = new(
        MaxFileBytes: 1_048_576,
        MaxFilesPerWorkspace: 50_000,
        MaxQueryLength: 200,
        MaxLinesPerFile: 20,
        MaxMatchingFiles: 1_000,
        MaxDisplayedLineChars: 240,
        PageSize: 50,
        MaxPageSize: 200,
        ScanBudgetSeconds: 20,
        MinifiedMinBytes: 4_000,
        MinifiedAverageLineChars: 300,
        VendoredFolders: ["wwwroot/lib", "dist", "vendor"]);

    public static ContentOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var defaults = Defaults;
        var options = new ContentOptions(
            ReadPositive(configuration, nameof(MaxFileBytes), defaults.MaxFileBytes),
            ReadPositive(configuration, nameof(MaxFilesPerWorkspace), defaults.MaxFilesPerWorkspace),
            ReadPositive(configuration, nameof(MaxQueryLength), defaults.MaxQueryLength),
            ReadPositive(configuration, nameof(MaxLinesPerFile), defaults.MaxLinesPerFile),
            ReadPositive(configuration, nameof(MaxMatchingFiles), defaults.MaxMatchingFiles),
            ReadPositive(configuration, nameof(MaxDisplayedLineChars), defaults.MaxDisplayedLineChars),
            ReadPositive(configuration, nameof(PageSize), defaults.PageSize),
            ReadPositive(configuration, nameof(MaxPageSize), defaults.MaxPageSize),
            ReadPositive(configuration, nameof(ScanBudgetSeconds), defaults.ScanBudgetSeconds),
            ReadPositive(configuration, nameof(MinifiedMinBytes), defaults.MinifiedMinBytes),
            ReadPositive(configuration, nameof(MinifiedAverageLineChars), defaults.MinifiedAverageLineChars),
            ReadFolders(configuration, defaults.VendoredFolders));
        return options.PageSize <= options.MaxPageSize
            ? options
            : throw new InvalidOperationException($"{Prefix}{nameof(PageSize)} must not exceed {Prefix}{nameof(MaxPageSize)}.");
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

    // Comma separated folder names or '/' paths, matched anywhere in a file's folder path. 'none' turns the rule off.
    private static IReadOnlyList<string> ReadFolders(IConfiguration configuration, IReadOnlyList<string> fallback)
    {
        var name = Prefix + nameof(VendoredFolders);
        var value = configuration[name];
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        if (value.Trim().Equals(NoFolders, StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var folders = new List<string>();
        foreach (var entry in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var folder = entry.Replace('\\', '/').Trim('/');
            if (folder.Contains(':') || folder.Split('/').Any(part => part.Length == 0 || part is "." or ".."))
            {
                throw new InvalidOperationException($"{name} holds an invalid folder '{entry}'.");
            }

            folders.Add(folder);
        }

        return folders;
    }
}
