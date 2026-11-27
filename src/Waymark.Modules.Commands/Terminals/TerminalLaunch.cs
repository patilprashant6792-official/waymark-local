using Waymark.Core.Commands;
using Waymark.Modules.Commands.Recipes;

namespace Waymark.Modules.Commands.Terminals;

/// <summary>
/// How a command starts in its terminal: which terminal, how to tell it is up (texts it prints, a port it listens on, and for a script, a quiet
/// moment after its first output), what means the app inside went down or came back, the build to run first, and the owner's earlier approval of
/// the same command, which spares asking again while nothing it depends on changed. <paramref name="ReadyOnAnyPort"/> lets any port the program
/// listens on count as up; it is off when Claude named the text to wait for.
/// </summary>
public sealed record TerminalLaunch(
    long TerminalId,
    string Name,
    bool IsShell,
    IReadOnlyList<string> ReadyPatterns,
    IReadOnlyList<string> AppDownPatterns,
    IReadOnlyList<string> AppUpPatterns,
    int? ReadyPort,
    TimeSpan ReadyTimeout,
    TimeSpan? MaxLifetime,
    CommandPlan? Prepare,
    TerminalApproval? Prior,
    bool ReadyOnAnyPort = true);
