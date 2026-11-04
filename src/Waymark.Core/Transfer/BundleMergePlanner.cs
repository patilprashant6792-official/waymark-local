using Waymark.Core.Memory;
using Waymark.Core.Security;

namespace Waymark.Core.Transfer;

/// <summary>
/// One feature map to write into a workspace, as a new map. The store decides again when it writes, because the workspace may have changed in between: a name that is taken by then is skipped.
/// Steps are in file order; Ordered is false for a map exported before flows existed.
/// </summary>
public sealed record MapMerge(string Name, string Summary, DateTimeOffset? UpdatedUtc, bool Ordered, IReadOnlyList<BundleComponent> Steps);

/// <summary>Exactly what an import adds to one workspace, and what it leaves as it is.</summary>
/// <param name="MapsConflicting">Maps whose name is here with other steps: left out, so the map in this workspace stays as it is.</param>
public sealed record WorkspaceMergePlan(
    IReadOnlyList<BundleRule> RulesToAdd,
    int RulesAlreadyThere,
    int RulesDifferent,
    int RulesOverLimit,
    IReadOnlyList<MapMerge> Maps,
    int MapsUnchanged,
    int MapsConflicting)
{
    public static WorkspaceMergePlan Empty { get; } = new([], 0, 0, 0, [], 0, 0);

    public int MapsNew => Maps.Count;

    public int StepsToAdd => Maps.Sum(map => map.Steps.Count);

    /// <summary>Rules that let tools open one more file type. They widen what Claude can read, so the owner is told before they are added.</summary>
    public int RulesAllowingFileTypes => RulesToAdd.Count(rule => rule is { Kind: AccessRuleKind.AllowExtension, Enabled: true });

    public bool HasChanges => RulesToAdd.Count > 0 || Maps.Count > 0;
}

/// <summary>
/// Works out what importing a workspace of a bundle changes in a workspace that exists already. Only adds: a rule that is there stays as the owner has it, and a feature map (flow)
/// whose name is there is never merged: the same steps count as already here, different steps are left out and counted, so the owner decides which one to keep.
/// Pure, so the preview the owner confirms is the same computation the import runs.
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
            : (Maps: new List<MapMerge>(), Unchanged: 0, Conflicting: 0);
        return new WorkspaceMergePlan(ruleCounts.Add, ruleCounts.Already, ruleCounts.Different, ruleCounts.Over, mapPlan.Maps, mapPlan.Unchanged, mapPlan.Conflicting);
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

    private static (List<MapMerge> Maps, int Unchanged, int Conflicting) PlanMaps(IReadOnlyList<BundleMap> incoming, IReadOnlyList<FeatureMap> existing)
    {
        var byName = existing.ToDictionary(map => map.Name, map => map, StringComparer.OrdinalIgnoreCase);
        var maps = new List<MapMerge>();
        int unchanged = 0, conflicting = 0;
        foreach (var map in incoming)
        {
            if (!byName.TryGetValue(map.Name, out var there))
            {
                maps.Add(new MapMerge(map.Name, map.Summary, map.UpdatedUtc, map.Ordered, map.Components));
                continue;
            }

            // A flow is never merged into another: two lists of steps cannot be combined without changing what the path means.
            if (SameSteps(map, there))
            {
                unchanged++;
            }
            else
            {
                conflicting++;
            }
        }

        return (maps, unchanged, conflicting);
    }

    /// <summary>Ordered flows are the same when they have the same steps at the same depth with the same links; unordered maps when they have the same components.</summary>
    private static bool SameSteps(BundleMap incoming, FeatureMap stored)
    {
        if (incoming.Ordered != stored.Ordered || incoming.Components.Count != stored.Steps.Count)
        {
            return false;
        }

        if (!incoming.Ordered)
        {
            var specs = stored.Steps.Select(step => step.Spec).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return incoming.Components.All(component => specs.Contains(component.Spec));
        }

        for (var i = 0; i < stored.Steps.Count; i++)
        {
            var left = incoming.Components[i];
            var right = stored.Steps[i];
            if (!string.Equals(left.Spec, right.Spec, StringComparison.OrdinalIgnoreCase)
                || left.Depth != right.Depth
                || !string.Equals(left.Link, right.Link, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static string RuleKey(AccessRuleKind kind, string pattern) => $"{kind}|{pattern}";
}
