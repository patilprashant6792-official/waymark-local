using Waymark.Core.Memory;
using Waymark.Core.Workspaces;

namespace Waymark.Modules.Code.Memory;

/// <summary>Lets the admin pages ask whether saved components are in the checked-out code, with the same reader that recall uses.</summary>
internal sealed class FeatureComponentChecker(FeatureComponentReader reader) : IFeatureComponentChecker
{
    public async Task<IReadOnlyList<FeatureComponentStatus>> CheckAsync(Workspace workspace, IReadOnlyList<string> specs, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(specs);
        var files = new Dictionary<string, FileView>(StringComparer.OrdinalIgnoreCase);
        var result = new List<FeatureComponentStatus>();
        foreach (var spec in specs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!FeatureComponentSpec.TryParse(spec, out var component, out _))
            {
                result.Add(new FeatureComponentStatus(spec, false, "the saved text is not a valid component"));
                continue;
            }

            var view = await reader.ReadAsync(workspace, component, files, ct).ConfigureAwait(false);
            result.Add(new FeatureComponentStatus(spec, view.Problem is null, view.Problem));
        }

        return result;
    }
}
