using System.Collections.Concurrent;
using Waymark.Core.Events;
using Waymark.Core.Memory;

namespace Waymark.Modules.Code.Memory.Semantic;

/// <summary>
/// The flows of each workspace with their steps, as the word search scores them. Loading them is the expensive part of a search (every flow, every step),
/// so they are kept until a flow of that workspace changes. Every writer of a flow publishes FlowChanged or FeatureMapsChanged.
/// </summary>
internal sealed class FlowWordCache : IDisposable
{
    private readonly IFeatureMapStore _maps;
    private readonly ConcurrentDictionary<long, Lazy<Task<IReadOnlyList<FeatureMap>>>> _flows = new();
    private readonly List<IDisposable> _subscriptions = [];

    public FlowWordCache(IFeatureMapStore maps, IEventBus events)
    {
        _maps = maps ?? throw new ArgumentNullException(nameof(maps));
        ArgumentNullException.ThrowIfNull(events);
        _subscriptions.Add(events.Subscribe<FlowChanged>(changed => Invalidate(changed.WorkspaceId)));
        _subscriptions.Add(events.Subscribe<FeatureMapsChanged>(changed => Invalidate(changed.WorkspaceId)));
        _subscriptions.Add(events.Subscribe<WorkspaceChanged>(changed => Invalidate(changed.WorkspaceId)));
    }

    public async Task<IReadOnlyList<FeatureMap>> GetAsync(long workspaceId, CancellationToken ct)
    {
        var entry = _flows.GetOrAdd(workspaceId, id => new Lazy<Task<IReadOnlyList<FeatureMap>>>(() => MemoryItems.ListFlowsAsync(_maps, id, CancellationToken.None)));
        try
        {
            return await entry.Value.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // A failed load is not kept: the next search tries again.
            _flows.TryRemove(new KeyValuePair<long, Lazy<Task<IReadOnlyList<FeatureMap>>>>(workspaceId, entry));
            throw;
        }
    }

    public void Dispose()
    {
        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }

        _subscriptions.Clear();
    }

    private ValueTask Invalidate(long workspaceId)
    {
        _flows.TryRemove(workspaceId, out _);
        return ValueTask.CompletedTask;
    }
}
