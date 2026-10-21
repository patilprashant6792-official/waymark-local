using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Waymark.Modules.Commands;

/// <summary>
/// Limits and folders of the commands module. Waymark:DataDirectory is the folder that also holds the database (default %LOCALAPPDATA%\Waymark);
/// outputs of runs are kept in its runs folder.
/// </summary>
public sealed record CommandsOptions(string RunsDirectory, TimeSpan ApprovalTimeout, int MaxParallelRuns, int RetentionDays)
{
    public const string DataDirectoryConfigKey = "Waymark:DataDirectory";
    public const int DefaultApprovalMinutes = 10;
    public const int DefaultMaxParallelRuns = 4;
    public const int DefaultRetentionDays = 30;

    public static CommandsOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var configured = configuration[DataDirectoryConfigKey];
        var root = string.IsNullOrWhiteSpace(configured) ? DefaultDataDirectory() : Path.GetFullPath(configured);
        var minutes = ReadPositive(configuration, "Waymark:Commands:ApprovalMinutes", DefaultApprovalMinutes);
        var parallel = ReadPositive(configuration, "Waymark:Commands:MaxParallelRuns", DefaultMaxParallelRuns);
        var retention = ReadPositive(configuration, "Waymark:Commands:RetentionDays", DefaultRetentionDays);
        return new CommandsOptions(Path.Combine(root, "runs"), TimeSpan.FromMinutes(minutes), parallel, retention);
    }

    private static string DefaultDataDirectory()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new InvalidOperationException("The local application data folder could not be determined; set " + DataDirectoryConfigKey + ".");
        }

        return Path.Combine(root, "Waymark");
    }

    private static int ReadPositive(IConfiguration configuration, string key, int fallback)
    {
        var value = configuration[key];
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 1)
        {
            throw new InvalidOperationException($"{key} must be a positive whole number.");
        }

        return number;
    }
}
