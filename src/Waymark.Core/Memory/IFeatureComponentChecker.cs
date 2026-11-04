using Waymark.Core.Workspaces;

namespace Waymark.Core.Memory;

/// <summary>
/// What the code index says about the link from a step to the step it is called by.
/// <see cref="DoesNotCall"/> is the only state that means the flow no longer matches the code: it is reported only when both steps are in the code that is checked out.
/// </summary>
public enum FlowLinkState
{
    /// <summary>The step above calls this one, on a type that can reach it.</summary>
    Calls,

    /// <summary>A call of that name is there, but its receiver type is unknown or only guessed.</summary>
    NameOnly,

    /// <summary>A link the code index cannot see (an event, an HTTP request, the interface, a registration), a constructor, or a file or line range; the agent declared it.</summary>
    Declared,

    /// <summary>Both steps are here and the step above makes no such call.</summary>
    DoesNotCall,

    /// <summary>One of the two steps is not in the code that is checked out (another branch, a rename, a deleted file), so the link cannot be judged. It is kept.</summary>
    NotOnThisBranch,
}

/// <summary>
/// Whether one saved step is in the code on disk now, and what the code says about the link to it. Problem says why a step is not there; MovedTo is where a method of that name is now, when one is.
/// <paramref name="Link"/> is null for the first step of a flow, which has no incoming link.
/// </summary>
public sealed record FeatureComponentStatus(
    string Spec,
    bool Available,
    string? Problem,
    FlowLinkState? Link = null,
    string? LinkDetail = null,
    string? MovedTo = null);

/// <summary>
/// Checks the steps of a saved flow against the code that is checked out, through the path guard, so access rules and blocked files apply as they do for the code tools.
/// It only reads. A step that is not there is reported, never repaired or dropped; a flow with steps from another branch stays as it is.
/// </summary>
public interface IFeatureComponentChecker
{
    /// <summary>One status per step of the map, in the order of its steps.</summary>
    Task<IReadOnlyList<FeatureComponentStatus>> CheckAsync(Workspace workspace, FeatureMap map, CancellationToken ct);

    /// <summary>The same for several maps, reading each file once for all of them. The key is the id of the map.</summary>
    Task<IReadOnlyDictionary<long, IReadOnlyList<FeatureComponentStatus>>> CheckManyAsync(Workspace workspace, IReadOnlyList<FeatureMap> maps, CancellationToken ct);
}
