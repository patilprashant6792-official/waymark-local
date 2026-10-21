namespace Waymark.Core.Commands;

/// <summary>A run was created or moved to another status.</summary>
public sealed record CommandRunChanged(long RunId, long? WorkspaceId, CommandRunStatus Status);

/// <summary>New output of a running command. <see cref="Seq"/> counts the chunks of that run from 1.</summary>
public sealed record CommandOutputChunk(long RunId, long? WorkspaceId, long Seq, string Text);

/// <summary>The owner changed a gate or forgot the trusted build files. <see cref="WorkspaceId"/> is <see cref="CommandCatalog.AllWorkspaces"/> for a rule that applies to every workspace.</summary>
public sealed record CommandPolicyChanged(long WorkspaceId);
