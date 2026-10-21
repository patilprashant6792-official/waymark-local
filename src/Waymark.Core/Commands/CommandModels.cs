namespace Waymark.Core.Commands;

/// <summary>What happens when Claude asks for an operation: it is refused (Off), waits for the owner (Ask) or runs at once (Auto).</summary>
public enum CommandGate
{
    Off,
    Ask,
    Auto
}

/// <summary>A recipe is a typed operation with checked arguments. A shell run is free text and is always approved by the owner.</summary>
public enum CommandTier
{
    Recipe,
    Shell
}

public enum CommandRunStatus
{
    WaitingApproval,
    Queued,
    Running,
    Succeeded,
    Failed,
    Denied,
    TimedOut,
    Cancelled
}

public static class CommandRunStatusExtensions
{
    /// <summary>True while the run can still change: it waits for approval, waits for its turn, or runs.</summary>
    public static bool IsActive(this CommandRunStatus status) =>
        status is CommandRunStatus.WaitingApproval or CommandRunStatus.Queued or CommandRunStatus.Running;
}

/// <summary>
/// One command Claude asked for. <see cref="WorkspaceId"/> is null for a run outside any workspace (a system shell run) and after its
/// workspace was removed; <see cref="WorkspaceName"/> keeps the name for the history.
/// </summary>
public sealed record CommandRun(
    long Id,
    long? WorkspaceId,
    string WorkspaceName,
    CommandTier Tier,
    string Operation,
    string? Shell,
    string CommandLine,
    string? OriginalCommand,
    string WorkingDirectory,
    string Purpose,
    CommandRunStatus Status,
    int? ExitCode,
    DateTimeOffset RequestedUtc,
    DateTimeOffset? DecidedUtc,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? EndedUtc,
    string Actor,
    string? DecidedBy,
    string? AskReason,
    string? Note,
    IReadOnlyList<string> Risks,
    long OutputBytes,
    string? OutputPath);

public sealed record NewCommandRun(
    long? WorkspaceId,
    string WorkspaceName,
    CommandTier Tier,
    string Operation,
    string? Shell,
    string CommandLine,
    string WorkingDirectory,
    string Purpose,
    CommandRunStatus Status,
    string Actor,
    string? AskReason,
    IReadOnlyList<string> Risks,
    DateTimeOffset RequestedUtc);

/// <summary>Fields left null keep their stored value.</summary>
public sealed record CommandRunUpdate
{
    public CommandRunStatus? Status { get; init; }

    public int? ExitCode { get; init; }

    public DateTimeOffset? DecidedUtc { get; init; }

    public string? DecidedBy { get; init; }

    public DateTimeOffset? StartedUtc { get; init; }

    public DateTimeOffset? EndedUtc { get; init; }

    public string? CommandLine { get; init; }

    public string? OriginalCommand { get; init; }

    public string? Note { get; init; }

    public long? OutputBytes { get; init; }

    public string? OutputPath { get; init; }
}

/// <summary>Newest first. <see cref="SystemOnly"/> lists the runs that belong to no workspace; it wins over <see cref="WorkspaceId"/>.</summary>
public sealed record CommandRunQuery(long? WorkspaceId = null, bool SystemOnly = false, CommandRunStatus? Status = null, long? BeforeId = null, int Limit = 50);

/// <summary>The output of a run. <see cref="Seq"/> is the number of the last output chunk included, so a live view can skip chunks it already has.</summary>
public sealed record RunOutputSnapshot(string Text, long Seq, bool IsLive);
