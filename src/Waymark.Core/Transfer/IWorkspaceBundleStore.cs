namespace Waymark.Core.Transfer;

/// <summary>The merge plan for one workspace that exists on this computer.</summary>
public sealed record WorkspaceApply(long WorkspaceId, WorkspaceMergePlan Plan);

/// <summary>What was written for one workspace.</summary>
public sealed record WorkspaceApplied(long WorkspaceId, int RulesAdded, int MapsAdded, int StepsAdded);

/// <summary>Writes the merge plans of an import.</summary>
public interface IWorkspaceBundleStore
{
    /// <summary>
    /// Writes every plan in one transaction: either all of it is stored or none. It only adds. A rule that exists is not touched; a map is created only when its name is free (a name that was taken
    /// since the plan was made is skipped, never merged); the limit of rules per workspace is checked again here. A map gets the time of the file as its last change.
    /// </summary>
    Task<IReadOnlyList<WorkspaceApplied>> ApplyAsync(IReadOnlyList<WorkspaceApply> items, DateTimeOffset now, CancellationToken ct);
}
