using Waymark.Core.Events;
using Waymark.Core.Memory;

namespace Waymark.Modules.Code.Memory.Semantic;

/// <summary>The live state of the embedding index, written by the sync and read by the Setup page. Every change publishes <see cref="MemoryIndexStatusChanged"/>.</summary>
internal sealed class MemoryIndexStatus(IEventBus events) : IMemoryIndexStatus
{
    private MemoryIndexSnapshot _current = new(MemoryIndexState.Off, 0, 0, null, null, null);

    public MemoryIndexSnapshot Current => Volatile.Read(ref _current);

    public async ValueTask SetAsync(Func<MemoryIndexSnapshot, MemoryIndexSnapshot> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        MemoryIndexSnapshot before;
        MemoryIndexSnapshot after;
        do
        {
            before = Volatile.Read(ref _current);
            after = change(before);
        }
        while (!ReferenceEquals(Interlocked.CompareExchange(ref _current, after, before), before));

        if (after != before)
        {
            await events.PublishAsync(new MemoryIndexStatusChanged()).ConfigureAwait(false);
        }
    }
}
