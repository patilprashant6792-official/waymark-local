using System.Collections.Concurrent;
using Waymark.Core.Events;

namespace Waymark.Core.Memory;

/// <summary>
/// The owner's pinned notes per workspace, as the workspace guide shows them and its hash covers them. Read on every tool call that checks the guide,
/// so they are kept in memory and dropped whenever a note, a flow or a workspace changes.
/// </summary>
public interface IPinnedNotes
{
    /// <summary>The pinned active notes of the workspace, oldest pin first.</summary>
    Task<IReadOnlyList<Note>> GetAsync(long workspaceId, CancellationToken ct);

    /// <summary>The workspaces that have pinned notes.</summary>
    Task<IReadOnlySet<long>> WorkspacesAsync(CancellationToken ct);
}

/// <inheritdoc cref="IPinnedNotes"/>
public sealed class PinnedNotes : IPinnedNotes, IDisposable
{
    private readonly INoteStore _store;
    private readonly IDisposable[] _subscriptions;
    private readonly ConcurrentDictionary<long, IReadOnlyList<Note>> _byWorkspace = new();
    private volatile IReadOnlySet<long>? _workspaces;

    // Raised by every change; a load that started before a change does not keep what it read.
    private long _generation;

    public PinnedNotes(INoteStore store, IEventBus events)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(events);
        _store = store;
        _subscriptions =
        [
            events.Subscribe<NoteChanged>(_ => Forget()),
            events.Subscribe<FeatureMapsChanged>(_ => Forget()),
            events.Subscribe<WorkspaceChanged>(_ => Forget()),
        ];
    }

    public async Task<IReadOnlyList<Note>> GetAsync(long workspaceId, CancellationToken ct)
    {
        if (_byWorkspace.TryGetValue(workspaceId, out var cached))
        {
            return cached;
        }

        var generation = Interlocked.Read(ref _generation);
        var loaded = await _store.ListPinnedAsync(workspaceId, ct).ConfigureAwait(false);
        if (Interlocked.Read(ref _generation) == generation)
        {
            _byWorkspace[workspaceId] = loaded;
        }

        return loaded;
    }

    public async Task<IReadOnlySet<long>> WorkspacesAsync(CancellationToken ct)
    {
        if (_workspaces is { } cached)
        {
            return cached;
        }

        var generation = Interlocked.Read(ref _generation);
        var loaded = (await _store.ListPinnedWorkspaceIdsAsync(ct).ConfigureAwait(false)).ToHashSet();
        if (Interlocked.Read(ref _generation) == generation)
        {
            _workspaces = loaded;
        }

        return loaded;
    }

    public void Dispose()
    {
        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }
    }

    private ValueTask Forget()
    {
        Interlocked.Increment(ref _generation);
        _byWorkspace.Clear();
        _workspaces = null;
        return ValueTask.CompletedTask;
    }
}
