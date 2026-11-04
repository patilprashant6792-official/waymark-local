namespace Waymark.Core.Memory;

/// <summary>What happened to a file whose steps may be in a flow.</summary>
public enum FlowEditKind
{
    /// <summary>Lines of the file were changed or the file was written.</summary>
    Edited,

    /// <summary>The file was moved or renamed: its steps are not at the old path any more.</summary>
    Moved,

    /// <summary>The file was deleted.</summary>
    Deleted,
}

/// <summary>
/// Tells the agent, in the result of a file tool, when the change it just made touches a step of a saved flow and the flow no longer matches the code.
/// The files module calls it after a successful change; the code module implements it. It only reads and never throws, so a file change is never held up or failed by it.
/// </summary>
public interface IFlowEditAdvisor
{
    /// <summary>Lines to add to the tool result, or null when there is nothing to say (most edits touch no flow, or leave every link as it was).</summary>
    Task<string?> AdviseAsync(string workspace, IReadOnlyList<string> relativePaths, FlowEditKind kind, CancellationToken ct);
}

/// <summary>The advisor of a host that has no flows (or that has the hint switched off): it never says anything.</summary>
public sealed class NoFlowEditAdvisor : IFlowEditAdvisor
{
    public Task<string?> AdviseAsync(string workspace, IReadOnlyList<string> relativePaths, FlowEditKind kind, CancellationToken ct) =>
        Task.FromResult<string?>(null);
}
