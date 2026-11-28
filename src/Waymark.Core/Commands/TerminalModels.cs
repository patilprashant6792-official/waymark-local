namespace Waymark.Core.Commands;

/// <summary>
/// Where a terminal stands. A terminal runs one program at a time, like a tab in an IDE's terminal panel: <see cref="Starting"/> until the program
/// says it is up (or the start phase ends), <see cref="Ready"/> while it serves, <see cref="AppDown"/> while the program still runs but the app
/// inside it stopped (a watcher waiting for a file change), and an ended state once the program is gone.
/// </summary>
public enum TerminalState
{
    Idle,
    Starting,
    Ready,
    AppDown,
    Exited,
    Crashed,
    Stopped,
    WaymarkStopped,
}

public static class TerminalStateExtensions
{
    /// <summary>True while a program runs in the terminal.</summary>
    public static bool IsLive(this TerminalState state) => state is TerminalState.Starting or TerminalState.Ready or TerminalState.AppDown;

    /// <summary>The word shown for the state, the same in the admin UI, the guides and the tool results.</summary>
    public static string Word(this TerminalState state) => state switch
    {
        TerminalState.Idle => "idle",
        TerminalState.Starting => "starting",
        TerminalState.Ready => "ready",
        TerminalState.AppDown => "app down",
        TerminalState.Exited => "exited",
        TerminalState.Crashed => "crashed",
        TerminalState.Stopped => "stopped",
        TerminalState.WaymarkStopped => "ended by a Waymark restart",
        _ => state.ToString().ToLowerInvariant(),
    };
}

/// <summary>Who did something to a terminal or a run. Stored as is.</summary>
public static class TerminalActors
{
    public const string Claude = "claude";
    public const string Owner = "owner";
    public const string Waymark = "waymark";
}

/// <summary>
/// The owner's approval of a terminal command, kept so that starting the same command again (the developer's up-arrow and Enter) does not ask again
/// while nothing that decides what runs has changed: the operation, the command line, the folder, the program and the project files.
/// </summary>
public sealed record TerminalApproval(
    string Operation,
    string CommandLine,
    string WorkingDirectory,
    string? ExecutablePath,
    string FingerprintJson,
    DateTimeOffset ApprovedUtc);

/// <summary>
/// Everything needed to start a terminal's command again after Waymark restarted: the checked program and its arguments (a recipe) or the script
/// (a shell run), plus how readiness is detected. It was checked when it was first requested; starting it again goes through the policy once more.
/// </summary>
public sealed record TerminalLaunchSpec(
    CommandTier Tier,
    string Operation,
    string CommandLine,
    string WorkingDirectory,
    string Purpose,
    string? Program,
    string? ExecutablePath,
    IReadOnlyList<string> Arguments,
    string? PrepareProgram,
    IReadOnlyList<string> PrepareArguments,
    string? ShellId,
    string? Script,
    IReadOnlyList<string> ReadyPatterns,
    IReadOnlyList<string> AppDownPatterns,
    IReadOnlyList<string> AppUpPatterns,
    int? ReadyPort,
    int ReadyTimeoutSeconds,
    int? MaxMinutes);

/// <summary>One terminal: a named slot of a workspace (or of the computer, when <see cref="WorkspaceId"/> is null) that runs one program at a time.</summary>
public sealed record Terminal(
    long Id,
    long? WorkspaceId,
    string WorkspaceName,
    string Name,
    string Folder,
    TerminalState State,
    long? CurrentRunId,
    long? LastRunId,
    string? Operation,
    string? CommandLine,
    IReadOnlyList<string> Urls,
    IReadOnlyList<int> Ports,
    int? Pid,
    DateTimeOffset? PidStartUtc,
    string? Program,
    int? ExitCode,
    string? StateNote,
    string? StoppedBy,
    string? ReadyHow,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? ReadyUtc,
    DateTimeOffset? EndedUtc,
    DateTimeOffset? LastOutputUtc,
    TerminalApproval? Approval,
    TerminalLaunchSpec? Launch,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc)
{
    /// <summary>The scope's name for messages: the workspace, or "system".</summary>
    public string Scope => WorkspaceId is null ? "system" : WorkspaceName;
}

public sealed record NewTerminal(long? WorkspaceId, string WorkspaceName, string Name, string Folder, DateTimeOffset CreatedUtc);

/// <summary>
/// A change to a terminal. Fields left null keep their stored value. <see cref="StartRun"/> clears what belonged to the previous program
/// (addresses, ports, exit code, notes, who stopped it, its times) before the new values are written.
/// </summary>
public sealed record TerminalUpdate
{
    public bool StartRun { get; init; }

    public bool ClearCurrentRun { get; init; }

    public bool ClearApproval { get; init; }

    public TerminalState? State { get; init; }

    public long? CurrentRunId { get; init; }

    public long? LastRunId { get; init; }

    public string? Operation { get; init; }

    public string? CommandLine { get; init; }

    public string? Folder { get; init; }

    public IReadOnlyList<string>? Urls { get; init; }

    public IReadOnlyList<int>? Ports { get; init; }

    public int? Pid { get; init; }

    public DateTimeOffset? PidStartUtc { get; init; }

    public string? Program { get; init; }

    public int? ExitCode { get; init; }

    public string? StateNote { get; init; }

    public string? StoppedBy { get; init; }

    public string? ReadyHow { get; init; }

    public DateTimeOffset? StartedUtc { get; init; }

    public DateTimeOffset? ReadyUtc { get; init; }

    public DateTimeOffset? EndedUtc { get; init; }

    public DateTimeOffset? LastOutputUtc { get; init; }

    public TerminalApproval? Approval { get; init; }

    public TerminalLaunchSpec? Launch { get; init; }
}

/// <summary>Which terminals to list. <see cref="SystemOnly"/> wins over <see cref="WorkspaceId"/>; neither lists every terminal.</summary>
public sealed record TerminalQuery(long? WorkspaceId = null, bool SystemOnly = false, bool LiveOnly = false);

/// <summary>
/// A terminal with what Waymark knows about it while it runs: errors printed since Claude last read it, the prompt it seems to wait at, and how far
/// Claude has read (the admin UI marks that line).
/// </summary>
public sealed record TerminalInfo(
    Terminal Terminal,
    int UnreadErrors,
    string? PendingPrompt,
    long ReadLine,
    DateTimeOffset? ReadUtc,
    long TotalLines);

/// <summary>What a terminal shows: the end of its earlier runs (each after a separator line) and the output of the run now, with that run's chunk number.</summary>
public sealed record TerminalOutput(string Text, long? RunId, long Seq, bool IsLive);
