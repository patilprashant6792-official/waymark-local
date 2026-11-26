using Waymark.Core.Code;
using Waymark.Core.Memory;

namespace Waymark.Modules.Code.Memory;

/// <summary>
/// For a method step that is not where it was saved, looks for a method of the same name (and type, when the step names one) in another file of the index.
/// For the agent and the owner it is a suggestion. The drift scanner may apply it, but only when there is exactly one such place (<see cref="FindAllAsync"/>).
/// </summary>
internal sealed class FlowDriftFinder(ICodeSearchStore store)
{
    private const int Candidates = 20;

    /// <summary>The canonical text of the step as it would be at the new place, or null when there is no such method elsewhere.</summary>
    public async Task<string?> FindAsync(long workspaceId, FeatureComponent component, CancellationToken ct)
    {
        var all = await FindAllAsync(workspaceId, component, ct).ConfigureAwait(false);
        return all.Count == 0 ? null : all[0];
    }

    /// <summary>Every other place a method of that name (and type) is, as step text, best first; one entry per file. Empty for a step that is not a method.</summary>
    public async Task<IReadOnlyList<string>> FindAllAsync(long workspaceId, FeatureComponent component, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(component);
        if (component.Member is null || component.IsRange)
        {
            return [];
        }

        var rows = await store.FindMembersByNameAsync(workspaceId, component.Member, Candidates, ct).ConfigureAwait(false);
        var found = new List<string>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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

            if (paths.Add(row.RelativePath))
            {
                found.Add(FeatureComponentSpec.Format(new FeatureComponent(row.RelativePath, component.Type, component.Member, null, null)));
            }
        }

        return found;
    }

    private static bool OwnerMatches(string? ownerFullName, string type) =>
        ownerFullName is not null
        && (string.Equals(ownerFullName, type, StringComparison.OrdinalIgnoreCase)
            || ownerFullName.EndsWith("." + type, StringComparison.OrdinalIgnoreCase));
}
