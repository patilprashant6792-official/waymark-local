using Waymark.Core.Commands;
using Waymark.Core.Workspaces;
using Waymark.Modules.Commands.Execution;
using Waymark.Modules.Commands.Recipes;

namespace Waymark.Modules.Commands;

/// <summary>
/// One checked request for a command. A recipe run carries its <see cref="Plan"/>; a shell run carries its <see cref="Shell"/> and <see cref="Script"/>.
/// <see cref="Workspace"/> is null for a shell run outside every workspace.
/// </summary>
public sealed record CommandSubmission(
    Workspace? Workspace,
    string WorkspaceName,
    OperationInfo Operation,
    CommandTier Tier,
    string CommandLine,
    string WorkingDirectory,
    string Purpose,
    string Actor,
    TimeSpan Timeout,
    CommandPlan? Plan,
    ShellInfo? Shell,
    string? Script,
    IReadOnlyList<string> Risks);

/// <summary>The stored run, and the reason when it was refused before it started (the operation is switched off).</summary>
public sealed record SubmitResult(CommandRun Run, string? RefusalReason)
{
    public bool IsRefused => RefusalReason is not null;
}
