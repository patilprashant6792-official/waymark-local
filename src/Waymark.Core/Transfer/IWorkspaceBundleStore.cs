namespace Waymark.Core.Transfer;

/// <summary>The merge plan for one workspace that exists on this computer.</summary>
public sealed record WorkspaceApply(long WorkspaceId, WorkspaceMergePlan Plan);

/// <summary>What was written for one workspace.</summary>
public sealed record WorkspaceApplied(long WorkspaceId, int RulesAdded, int MapsAdded, int MapsExtended, int ComponentsAdded);

/// <summary>Writes the merge plans of an import.</summary>
public interface IWorkspaceBundleStore
{
    /// <summary>
    /// Writes every plan in one transaction: either all of it is stored or none. It only adds. A rule that exists is not touched; a map that exists gets the components it lacks, up to the
    /// limit of a map; the limit of rules per workspace is checked again here. A map that is new gets the time of the file as its last change, a map that grew gets <paramref name="now"/>.
    /// </summary>
    Task<IReadOnlyList<WorkspaceApplied>> ApplyAsync(IReadOnlyList<WorkspaceApply> items, DateTimeOffset now, CancellationToken ct);
}
