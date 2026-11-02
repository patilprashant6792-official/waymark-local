using Waymark.Core.Code;
using Waymark.Core.Memory;

namespace Waymark.Modules.Code.Memory;

/// <summary>
/// For a method step that is not where it was saved, looks for a method of the same name (and type, when the step names one) in another file of the index and suggests it.
/// A suggestion only: it is shown to the agent and the owner, never written into a flow.
/// </summary>
internal sealed class FlowDriftFinder(ICodeSearchStore store)
{
    private const int Candidates = 20;

    /// <summary>The canonical text of the step as it would be at the new place, or null when there is no such method elsewhere.</summary>
    public async Task<string?> FindAsync(long workspaceId, FeatureComponent component, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(component);
        if (component.Member is null || component.IsRange)
        {
            return null;
        }

        var rows = await store.FindMembersByNameAsync(workspaceId, component.Member, Candidates, ct).ConfigureAwait(false);
        foreach (var row in rows)
        {
            if (string.Equals(row.RelativePath, component.Path, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (component.Type is not null && !OwnerMatches(row.OwnerFullName, component.Type))
            {
                continue;
            }

            return FeatureComponentSpec.Format(new FeatureComponent(row.RelativePath, component.Type, component.Member, null, null));
        }

        return null;
    }

    private static bool OwnerMatches(string? ownerFullName, string type) =>
        ownerFullName is not null
        && (string.Equals(ownerFullName, type, StringComparison.OrdinalIgnoreCase)
            || ownerFullName.EndsWith("." + type, StringComparison.OrdinalIgnoreCase));
}
