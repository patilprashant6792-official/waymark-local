using System.Text.RegularExpressions;

namespace Waymark.Host.Ui;

/// <summary>
/// A command line as the lists show it on one row. The fixed flags Waymark puts on every git call (no pager, the safety settings) are left out,
/// so the real subcommand is what you read first. The full line stays in the output and in Copy command.
/// </summary>
public static partial class CommandLineDisplay
{
    public static string Compact(string commandLine, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(commandLine);
        var line = GitFlags().Replace(commandLine.ReplaceLineEndings(" ").Trim(), "git");
        return line.Length <= maxChars ? line : line[..maxChars] + "...";
    }

    [GeneratedRegex(@"^git(?:\s+(?:--no-pager|-c\s+\S+))+")]
    private static partial Regex GitFlags();
}
