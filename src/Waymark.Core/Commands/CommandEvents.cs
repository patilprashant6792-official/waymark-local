namespace Waymark.Core.Commands;

/// <summary>A run was created or moved to another status.</summary>
public sealed record CommandRunChanged(long RunId, long? WorkspaceId, CommandRunStatus Status);

/// <summary>New output of a running command. <see cref="Seq"/> counts the chunks of that run from 1.</summary>
public sealed record CommandOutputChunk(long RunId, long? WorkspaceId, long Seq, string Text);

/// <summary>The owner changed a gate or forgot the trusted build files. <see cref="WorkspaceId"/> is <see cref="CommandCatalog.AllWorkspaces"/> for a rule that applies to every workspace.</summary>
public sealed record CommandPolicyChanged(long WorkspaceId);

/// <summary>A terminal was created, changed state or address, or was closed (<paramref name="Closed"/>).</summary>
public sealed record TerminalChanged(long TerminalId, long? WorkspaceId, TerminalState State, bool Closed = false);

public enum TerminalNoticeKind
{
    /// <summary>The program ended by itself with an error.</summary>
    Crashed,

    /// <summary>The program still runs but the app inside it stopped (a watcher waits for a file change).</summary>
    AppDown,

    /// <summary>The owner stopped the terminal in the admin UI.</summary>
    StoppedByOwner,

    /// <summary>New errors were printed.</summary>
    NewErrors,

    /// <summary>The program seems to wait for an answer.</summary>
    WaitsForInput,

    /// <summary>The program ended by itself without an error.</summary>
    Exited,
}

/// <summary>Something about a terminal that Claude should hear on its next call. <paramref name="Seq"/> rises with every notice.</summary>
public sealed record TerminalNotice(long TerminalId, long? WorkspaceId, string TerminalName, TerminalNoticeKind Kind, string Text, long Seq, DateTimeOffset AtUtc);
