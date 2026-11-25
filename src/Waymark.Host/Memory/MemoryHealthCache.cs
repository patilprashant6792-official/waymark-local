using System.Collections.Concurrent;
using Waymark.Core.Events;

namespace Waymark.Host.Memory;

/// <summary>The expensive memory figures of one workspace as the pages last computed them, with when. Null means not checked.</summary>
public sealed record MemoryHealth(int? StaleNotes, DateTimeOffset? StaleCheckedUtc, int? BrokenLinks, int? MissingSteps, DateTimeOffset? LinksCheckedUtc)
{
    public static MemoryHealth Unknown { get; } = new(null, null, null, null, null);
}

/// <summary>
/// Keeps the figures that need the code read (notes that may be stale, links that do not call, steps not on this branch) after the Notes and Flows pages
/// computed them, so the workspace Overview can show them without doing that work. It never computes anything itself. A figure is dropped after
/// <see cref="Lifetime"/> and whenever a flow, a note or a file of the workspace changes.
/// </summary>
public sealed class MemoryHealthCache : IDisposable
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<long, MemoryHealth> _health = new();
    private readonly List<IDisposable> _subscriptions = [];
    private readonly TimeProvider _time;

    public MemoryHealthCache(IEventBus events, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(events);
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _subscriptions.Add(events.Subscribe<FeatureMapsChanged>(changed => Forget(changed.WorkspaceId, links: true, stale: true)));
        _subscriptions.Add(events.Subscribe<NoteChanged>(changed => Forget(changed.WorkspaceId, links: false, stale: true)));
        _subscriptions.Add(events.Subscribe<FileChanged>(changed => Forget(changed.WorkspaceId, links: true, stale: true)));
        _subscriptions.Add(events.Subscribe<FolderChanged>(changed => Forget(changed.WorkspaceId, links: true, stale: true)));
    }

    /// <summary>The figures still fresh for the workspace.</summary>
    public MemoryHealth Get(long workspaceId)
    {
        if (!_health.TryGetValue(workspaceId, out var health))
        {
            return MemoryHealth.Unknown;
        }

        var now = _time.GetUtcNow();
        var staleFresh = health.StaleCheckedUtc is { } staleAt && now - staleAt < Lifetime;
        var linksFresh = health.LinksCheckedUtc is { } linksAt && now - linksAt < Lifetime;
        return new MemoryHealth(
            staleFresh ? health.StaleNotes : null,
            staleFresh ? health.StaleCheckedUtc : null,
            linksFresh ? health.BrokenLinks : null,
            linksFresh ? health.MissingSteps : null,
            linksFresh ? health.LinksCheckedUtc : null);
    }

    /// <summary>The Notes page checked every active anchored note of the workspace.</summary>
    public void SetStale(long workspaceId, int staleNotes) =>
        _health.AddOrUpdate(
            workspaceId,
            _ => MemoryHealth.Unknown with { StaleNotes = staleNotes, StaleCheckedUtc = _time.GetUtcNow() },
            (_, old) => old with { StaleNotes = staleNotes, StaleCheckedUtc = _time.GetUtcNow() });

    /// <summary>The Flows page checked every flow of the workspace against the code.</summary>
    public void SetLinks(long workspaceId, int brokenLinks, int missingSteps) =>
        _health.AddOrUpdate(
            workspaceId,
            _ => MemoryHealth.Unknown with { BrokenLinks = brokenLinks, MissingSteps = missingSteps, LinksCheckedUtc = _time.GetUtcNow() },
            (_, old) => old with { BrokenLinks = brokenLinks, MissingSteps = missingSteps, LinksCheckedUtc = _time.GetUtcNow() });

    public void Dispose()
    {
        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }

        _subscriptions.Clear();
    }

    private ValueTask Forget(long workspaceId, bool links, bool stale)
    {
        if (_health.TryGetValue(workspaceId, out var health))
        {
            var kept = health with
            {
                StaleNotes = stale ? null : health.StaleNotes,
                StaleCheckedUtc = stale ? null : health.StaleCheckedUtc,
                BrokenLinks = links ? null : health.BrokenLinks,
                MissingSteps = links ? null : health.MissingSteps,
                LinksCheckedUtc = links ? null : health.LinksCheckedUtc,
            };
            _health[workspaceId] = kept;
        }

        return ValueTask.CompletedTask;
    }
}
