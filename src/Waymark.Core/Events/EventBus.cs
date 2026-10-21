using Microsoft.Extensions.Logging;

namespace Waymark.Core.Events;

public sealed class EventBus(ILogger<EventBus> logger) : IEventBus
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Type, List<Delegate>> _handlers = [];

    public IDisposable Subscribe<TEvent>(Func<TEvent, ValueTask> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            if (!_handlers.TryGetValue(typeof(TEvent), out var list))
            {
                list = [];
                _handlers[typeof(TEvent)] = list;
            }

            list.Add(handler);
        }

        return new Subscription(() =>
        {
            lock (_gate)
            {
                if (_handlers.TryGetValue(typeof(TEvent), out var list))
                {
                    list.Remove(handler);
                }
            }
        });
    }

    public async ValueTask PublishAsync<TEvent>(TEvent evt)
    {
        Delegate[] snapshot;
        lock (_gate)
        {
            snapshot = _handlers.TryGetValue(typeof(TEvent), out var list) ? [.. list] : [];
        }

        foreach (var handler in snapshot)
        {
            try
            {
                await ((Func<TEvent, ValueTask>)handler)(evt);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "A handler for event {Event} failed", typeof(TEvent).Name);
            }
        }
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
