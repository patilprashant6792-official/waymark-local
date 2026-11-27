using System.Globalization;
using System.Text;
using Waymark.Core.Commands;
using Waymark.Core.Security;
using Waymark.Modules.Commands.Reporting;

namespace Waymark.Host.Mcp;

/// <summary>
/// How the guides speak of terminals: a block in a workspace's guide (and one for the computer's system terminals in the overview) with each terminal's
/// state, command, address, uptime and unread error lines, and short marks on the overview's workspace lines. Command lines can hold a secret given as
/// an argument, so every line passes the secret scanner: guide text is added after the result's own secret check.
/// </summary>
public static class TerminalGuideText
{
    private const int MaxListed = 12;

    /// <summary>The lines of a terminal block, each starting with "  - "; "none" when there are no terminals.</summary>
    public static string Block(IReadOnlyList<TerminalInfo> terminals, DateTimeOffset now, string readCall)
    {
        ArgumentNullException.ThrowIfNull(terminals);
        var text = new StringBuilder();
        if (terminals.Count == 0)
        {
            return "none yet (a run action, or cmd_shell with 'terminal', starts one)";
        }

        text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"{readCall}"));
        var ordered = terminals
            .OrderByDescending(t => t.Terminal.State.IsLive())
            .ThenByDescending(t => t.Terminal.UpdatedUtc)
            .ToList();
        foreach (var info in ordered.Take(MaxListed))
        {
            text.Append("  - ").AppendLine(Safe(TerminalReport.Line(info, now)));
        }

        if (ordered.Count > MaxListed)
        {
            text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  - and {ordered.Count - MaxListed} more: cmd_terminal(action='list')"));
        }

        return text.ToString().TrimEnd();
    }

    /// <summary>The marks a workspace line of the overview carries: "🖥 2 live", "✖ 1 crashed". Empty when nothing runs or crashed.</summary>
    public static string Marks(IEnumerable<TerminalInfo> terminals)
    {
        ArgumentNullException.ThrowIfNull(terminals);
        var list = terminals.ToList();
        var live = list.Count(t => t.Terminal.State.IsLive());
        var crashed = list.Count(t => t.Terminal.State == TerminalState.Crashed);
        var marks = new StringBuilder();
        if (live > 0)
        {
            marks.Append(string.Create(CultureInfo.InvariantCulture, $" · 🖥 {live} terminal{(live == 1 ? string.Empty : "s")} running"));
        }

        if (crashed > 0)
        {
            marks.Append(string.Create(CultureInfo.InvariantCulture, $" · ✖ {crashed} crashed"));
        }

        return marks.ToString();
    }

    private static string Safe(string line) => SensitiveDataScanner.Redact(line).Text;
}
