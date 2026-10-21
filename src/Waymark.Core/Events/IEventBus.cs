namespace Waymark.Core.Events;

/// <summary>In-process publish/subscribe. A failing handler is logged and never stops the publisher or other handlers.</summary>
public interface IEventBus
{
    /// <summary>Dispose the result to unsubscribe.</summary>
    IDisposable Subscribe<TEvent>(Func<TEvent, ValueTask> handler);

    ValueTask PublishAsync<TEvent>(TEvent evt);
}
