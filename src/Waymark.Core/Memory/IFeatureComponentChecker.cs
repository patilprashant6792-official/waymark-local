using Waymark.Core.Workspaces;

namespace Waymark.Core.Memory;

/// <summary>Whether one saved component is in the code on disk now. Problem says why not.</summary>
public sealed record FeatureComponentStatus(string Spec, bool Available, string? Problem);

/// <summary>
/// Checks saved components against the code that is checked out, through the path guard, so access rules and blocked files apply as they do for the code tools.
/// It only reads. A component that is not there is reported, never repaired or dropped.
/// </summary>
public interface IFeatureComponentChecker
{
    /// <summary>One status per distinct spec, in the order given.</summary>
    Task<IReadOnlyList<FeatureComponentStatus>> CheckAsync(Workspace workspace, IReadOnlyList<string> specs, CancellationToken ct);
}
