using Microsoft.AspNetCore.Components;
using Waymark.Core.Events;

namespace Waymark.Host.Components.Shared;

/// <summary>
/// Base for pages that update live. Event subscriptions are released and in-flight work is cancelled when the
/// circuit goes away, so a closed browser tab never leaves handlers behind.
/// </summary>
public abstract class LiveComponentBase : ComponentBase, IDisposable
{
    private readonly List<IDisposable> _subscriptions = [];
    private readonly CancellationTokenSource _cancellation = new();

    [Inject]
    protected IEventBus Events { get; set; } = default!;

    /// <summary>Cancelled when the component is disposed; pass it to every awaited call.</summary>
    protected CancellationToken Cancel => _cancellation.Token;

    /// <summary>Runs the reload and re-renders whenever the event is published.</summary>
    protected void RefreshOn<TEvent>(Func<Task> reload)
    {
        ArgumentNullException.ThrowIfNull(reload);
        RefreshOn<TEvent>(_ => reload());
    }

    /// <summary>Like <see cref="RefreshOn{TEvent}(Func{Task})"/>, but the handler receives the event.</summary>
    protected void RefreshOn<TEvent>(Func<TEvent, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _subscriptions.Add(Events.Subscribe<TEvent>(async message =>
        {
            try
            {
                await InvokeAsync(async () =>
                {
                    await handler(message);
                    StateHasChanged();
                });
            }
            catch (ObjectDisposedException)
            {
                // The circuit ended while the event was in flight.
            }
        }));
    }


    public virtual void Dispose()
    {
        _cancellation.Cancel();
        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }

        _cancellation.Dispose();
        GC.SuppressFinalize(this);
    }
}
