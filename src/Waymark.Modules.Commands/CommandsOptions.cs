using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Waymark.Modules.Commands;

/// <summary>
/// Limits and folders of the commands module. Waymark:DataDirectory is the folder that also holds the database (default %LOCALAPPDATA%\Waymark);
/// outputs of runs are kept in its runs folder. The terminal settings bound what keeps running: how many programs at once, how long one may hold
/// the workspace's turn while it starts, how long a stop waits for a clean shutdown, how much of its output is kept on disk and for how long an
/// approval of a terminal command is reused.
/// </summary>
public sealed record CommandsOptions(string RunsDirectory, TimeSpan ApprovalTimeout, int MaxParallelRuns, int RetentionDays)
{
    public const string DataDirectoryConfigKey = "Waymark:DataDirectory";
    public const int DefaultApprovalMinutes = 10;
    public const int DefaultMaxParallelRuns = 4;
    public const int DefaultRetentionDays = 30;

    /// <summary>How many terminals may run a program at the same time.</summary>
    public int MaxLiveTerminals { get; init; } = 8;

    /// <summary>The longest a terminal program holds the workspace's turn and a run slot while it starts, before it counts as started.</summary>
    public TimeSpan TerminalStartLock { get; init; } = TimeSpan.FromSeconds(180);

    /// <summary>How long a stop waits after Ctrl+C before it asks the program to terminate.</summary>
    public TimeSpan GracefulStop { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>The size at which a terminal's log file rotates, and how many files (the current one included) are kept.</summary>
    public long TerminalLogMaxBytes { get; init; } = 20L * 1024 * 1024;

    public int TerminalLogFiles { get; init; } = 3;

    /// <summary>For how long the owner's approval of a terminal command is reused when the same command starts again unchanged.</summary>
    public TimeSpan TerminalApprovalAge { get; init; } = TimeSpan.FromDays(7);

    public static CommandsOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var configured = configuration[DataDirectoryConfigKey];
        var root = string.IsNullOrWhiteSpace(configured) ? DefaultDataDirectory() : Path.GetFullPath(configured);
        var minutes = ReadPositive(configuration, "Waymark:Commands:ApprovalMinutes", DefaultApprovalMinutes);
        var parallel = ReadPositive(configuration, "Waymark:Commands:MaxParallelRuns", DefaultMaxParallelRuns);
        var retention = ReadPositive(configuration, "Waymark:Commands:RetentionDays", DefaultRetentionDays);
        return new CommandsOptions(Path.Combine(root, "runs"), TimeSpan.FromMinutes(minutes), parallel, retention)
        {
            MaxLiveTerminals = ReadPositive(configuration, "Waymark:Commands:MaxLiveTerminals", 8),
            TerminalStartLock = TimeSpan.FromSeconds(ReadPositive(configuration, "Waymark:Commands:TerminalStartLockSeconds", 180)),
            GracefulStop = TimeSpan.FromSeconds(ReadPositive(configuration, "Waymark:Commands:GracefulStopSeconds", 5)),
            TerminalLogMaxBytes = ReadPositive(configuration, "Waymark:Commands:TerminalLogMaxMegabytes", 20) * 1024L * 1024,
            TerminalLogFiles = ReadPositive(configuration, "Waymark:Commands:TerminalLogFiles", 3),
            TerminalApprovalAge = TimeSpan.FromDays(ReadPositive(configuration, "Waymark:Commands:TerminalApprovalDays", 7)),
        };
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
