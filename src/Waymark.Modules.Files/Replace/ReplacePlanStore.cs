using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Waymark.Modules.Files.Replace;

/// <summary>
/// Keeps the plans of file_replace previews in memory: each expires after PlanMinutes, is used at most once, and past MaxOpenPlans the plan
/// closest to expiry is dropped. A restart forgets every plan; the agent then runs the preview again.
/// </summary>
internal sealed class ReplacePlanStore(ReplaceOptions options, TimeProvider time)
{
    private const int IdBytes = 16;
    private readonly ConcurrentDictionary<string, ReplacePlan> _plans = new(StringComparer.Ordinal);

    public int Count => _plans.Count;

    public ReplacePlan Add(long workspaceId, string signature, IReadOnlyList<FilePlan> files)
    {
        ArgumentNullException.ThrowIfNull(signature);
        ArgumentNullException.ThrowIfNull(files);
        Prune();
        var id = "rp_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(IdBytes));
        var plan = new ReplacePlan(id, workspaceId, signature, files, time.GetUtcNow().AddMinutes(options.PlanMinutes));
        _plans[id] = plan;
        while (_plans.Count > options.MaxOpenPlans)
        {
            var oldest = _plans.Values.OrderBy(p => p.ExpiresUtc).First();
            _plans.TryRemove(oldest.Id, out _);
        }

        return plan;
    }

    /// <summary>The plan, or null when it is unknown, expired or already used.</summary>
    public ReplacePlan? Find(string id)
    {
        Prune();
        return _plans.TryGetValue(id, out var plan) ? plan : null;
    }

    /// <summary>Takes the plan out so it cannot be used again. False when another call took it first.</summary>
    public bool Remove(string id) => _plans.TryRemove(id, out _);

    private void Prune()
    {
        var now = time.GetUtcNow();
        foreach (var plan in _plans.Values)
        {
            if (plan.ExpiresUtc <= now)
            {
                _plans.TryRemove(plan.Id, out _);
            }
        }
    }
}
