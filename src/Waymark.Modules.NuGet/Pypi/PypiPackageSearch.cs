using Waymark.Modules.NuGet.Loading;

namespace Waymark.Modules.NuGet.Pypi;

/// <summary>
/// PyPI has no search service any more (only the exact JSON lookup of one project), so a query is tried as project names: the words joined with '-',
/// then each word. A hit is a project that exists under that name, with its latest version and summary.
/// </summary>
internal sealed class PypiPackageSearch(PypiClient client)
{
    private const int MaxCandidates = 6;

    private static readonly char[] Separators = [' ', ',', ';', '\t'];

    public async Task<IReadOnlyList<PackageSearchHit>> SearchAsync(string query, int take, CancellationToken ct)
    {
        var words = query.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var candidates = new List<string>();
        if (words.Count > 1)
        {
            candidates.Add(string.Join('-', words));
        }

        candidates.AddRange(words);
        var hits = new List<PackageSearchHit>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxCandidates))
        {
            if (!PypiNames.IsProject(candidate))
            {
                continue;
            }

            var project = await client.GetAsync(PypiNames.Normalize(candidate), null, ct).ConfigureAwait(false);
            if (project is not null && seen.Add(PypiNames.Normalize(project.Name)))
            {
                hits.Add(new PackageSearchHit(project.Name, project.Version, null, project.Summary ?? string.Empty));
            }

            if (hits.Count >= take)
            {
                break;
            }
        }

        return hits;
    }
}
