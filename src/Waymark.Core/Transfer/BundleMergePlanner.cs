using Waymark.Core.Memory;
using Waymark.Core.Security;

namespace Waymark.Core.Transfer;

/// <summary>
/// One feature map to write into a workspace. IsNew is what the plan saw; the store decides again when it writes, because the workspace may have changed in between.
/// Components are the ones to add, in file order; a component that is stored already is never listed.
/// </summary>
public sealed record MapMerge(string Name, string Summary, DateTimeOffset? UpdatedUtc, bool IsNew, IReadOnlyList<BundleComponent> Components);

/// <summary>Exactly what an import adds to one workspace, and what it leaves as it is.</summary>
public sealed record WorkspaceMergePlan(
    IReadOnlyList<BundleRule> RulesToAdd,
    int RulesAlreadyThere,
    int RulesDifferent,
    int RulesOverLimit,
    IReadOnlyList<MapMerge> Maps,
    int MapsUnchanged,
    int ComponentsOverLimit)
{
    public static WorkspaceMergePlan Empty { get; } = new([], 0, 0, 0, [], 0, 0);

    public int MapsNew => Maps.Count(map => map.IsNew);

    public int MapsExtended => Maps.Count(map => !map.IsNew);

    public int ComponentsToAdd => Maps.Sum(map => map.Components.Count);

    /// <summary>Rules that let tools open one more file type. They widen what Claude can read, so the owner is told before they are added.</summary>
    public int RulesAllowingFileTypes => RulesToAdd.Count(rule => rule is { Kind: AccessRuleKind.AllowExtension, Enabled: true });

    public bool HasChanges => RulesToAdd.Count > 0 || Maps.Count > 0;
}

/// <summary>
/// Works out what importing a workspace of a bundle changes in a workspace that exists already. Only adds: a rule or a map that is there stays as the owner has it, a feature map
/// that exists gets only the components it lacks (up to the limit of a map). Pure, so the preview the owner confirms is the same computation the import runs.
/// </summary>
public static class BundleMergePlanner
{
    public static WorkspaceMergePlan Plan(
        BundleWorkspace incoming,
        BundleContent content,
        IReadOnlyList<AccessRule> existingRules,
        IReadOnlyList<FeatureMap> existingMaps)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        ArgumentNullException.ThrowIfNull(existingRules);
        ArgumentNullException.ThrowIfNull(existingMaps);

        var ruleCounts = content.HasFlag(BundleContent.AccessRules)
            ? PlanRules(incoming.AccessRules, existingRules)
            : (Add: new List<BundleRule>(), Already: 0, Different: 0, Over: 0);
        var mapPlan = content.HasFlag(BundleContent.FeatureMaps)
            ? PlanMaps(incoming.FeatureMaps, existingMaps)
            : (Maps: new List<MapMerge>(), Unchanged: 0, Over: 0);
        return new WorkspaceMergePlan(ruleCounts.Add, ruleCounts.Already, ruleCounts.Different, ruleCounts.Over, mapPlan.Maps, mapPlan.Unchanged, mapPlan.Over);
    }

    private static (List<BundleRule> Add, int Already, int Different, int Over) PlanRules(IReadOnlyList<BundleRule> incoming, IReadOnlyList<AccessRule> existing)
    {
        var byKey = existing.ToDictionary(rule => RuleKey(rule.Kind, rule.Pattern), rule => rule, StringComparer.OrdinalIgnoreCase);
        var add = new List<BundleRule>();
        int already = 0, different = 0, over = 0;
        foreach (var rule in incoming)
        {
            if (byKey.TryGetValue(RuleKey(rule.Kind, rule.Pattern), out var there))
            {
                already++;
                if (there.Enabled != rule.Enabled)
                {
                    different++;
                }
            }
            else if (existing.Count + add.Count >= AccessRuleRules.MaxRulesPerWorkspace)
            {
                over++;
            }
            else
            {
                add.Add(rule);
            }
        }

        return (add, already, different, over);
    }

    private static (List<MapMerge> Maps, int Unchanged, int Over) PlanMaps(IReadOnlyList<BundleMap> incoming, IReadOnlyList<FeatureMap> existing)
    {
        var byName = existing.ToDictionary(map => map.Name, map => map, StringComparer.OrdinalIgnoreCase);
        var maps = new List<MapMerge>();
        int unchanged = 0, over = 0;
        foreach (var map in incoming)
        {
            if (!byName.TryGetValue(map.Name, out var there))
            {
                maps.Add(new MapMerge(map.Name, map.Summary, map.UpdatedUtc, true, map.Components));
                continue;
            }

            var stored = there.Components.Select(component => component.Spec).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missing = map.Components.Where(component => !stored.Contains(component.Spec)).ToList();
            if (missing.Count == 0)
            {
                unchanged++;
                continue;
            }

            var room = Math.Max(0, FeatureMapRules.MaxComponentsPerMap - there.Components.Count);
            over += Math.Max(0, missing.Count - room);
            if (room == 0)
            {
                unchanged++;
                continue;
            }

            maps.Add(new MapMerge(map.Name, map.Summary, map.UpdatedUtc, false, missing.Take(room).ToList()));
        }

        return (maps, unchanged, over);
    }

    private static string RuleKey(AccessRuleKind kind, string pattern) => $"{kind}|{pattern}";
}
