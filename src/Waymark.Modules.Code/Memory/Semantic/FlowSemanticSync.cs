using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Waymark.Core.Events;
using Waymark.Core.Memory;

namespace Waymark.Modules.Code.Memory.Semantic;

/// <summary>
/// Keeps the semantic index in step with the flows. Everything that writes a flow (memorise, the admin page, an import) publishes <see cref="FlowChanged"/>, and this is the one place that listens,
/// so no writer can forget the index. While the index is the empty one (<see cref="IFlowSemanticIndex.IsEnabled"/> false) it does nothing at all, not even a read.
/// </summary>
internal sealed class FlowSemanticSync(IEventBus events, IFeatureMapStore store, IFlowSemanticIndex index, ILogger<FlowSemanticSync> logger) : IHostedService
{
    private IDisposable? _subscription;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = events.Subscribe<FlowChanged>(OnChangedAsync);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        _subscription = null;
        return Task.CompletedTask;
    }

    private async ValueTask OnChangedAsync(FlowChanged changed)
    {
        if (!index.IsEnabled)
        {
            return;
        }

        try
        {
            switch (changed.Change)
            {
                case FlowChange.Saved:
                    var maps = await store.GetAsync(changed.WorkspaceId, [changed.FlowId], CancellationToken.None).ConfigureAwait(false);
                    if (maps.Count > 0)
                    {
                        await index.IndexAsync(FlowSemanticText.Compose(changed.WorkspaceId, maps[0]), CancellationToken.None).ConfigureAwait(false);
                    }

                    break;
                case FlowChange.Deleted:
                    await index.RemoveAsync(changed.WorkspaceId, changed.FlowId, CancellationToken.None).ConfigureAwait(false);
                    break;
                case FlowChange.Imported:
                    await index.RebuildAsync(changed.WorkspaceId, CancellationToken.None).ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The bus would log it too, but only this knows which flow and why it matters.
            logger.LogWarning(ex, "The semantic index could not follow flow {FlowId} of workspace {WorkspaceId} ({Change}).", changed.FlowId, changed.WorkspaceId, changed.Change);
        }
    }
}
