using System.Collections.Concurrent;
using Waymark.Core.Events;
using Waymark.Core.Security;
using Waymark.Core.Workspaces;

namespace Waymark.Core.Guides;

/// <inheritdoc cref="IWorkspaceGuides"/>
public sealed class WorkspaceGuides : IWorkspaceGuides, IDisposable
{
    private readonly IWorkspaceGuideStore _store;
    private readonly IEventBus _events;
    private readonly TimeProvider _time;
    private readonly IDisposable _subscription;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<long, WorkspaceGuideText> _texts = new();
    private volatile bool _loaded;

    public WorkspaceGuides(IWorkspaceGuideStore store, IEventBus events, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(time);
        _store = store;
        _events = events;
        _time = time;

        // The row itself goes with the workspace (the table cascades); the copy kept here must go too, or a new workspace that gets the same id
        // would inherit the old instructions.
        _subscription = events.Subscribe<WorkspaceChanged>(change =>
        {
            if (change.Kind == WorkspaceChangeKind.Removed)
            {
                Forget(change.WorkspaceId);
            }

            return ValueTask.CompletedTask;
        });
    }

    public async Task<WorkspaceGuideText> GetAsync(long workspaceId, CancellationToken ct)
    {
        await EnsureLoadedAsync(ct);
        return _texts.TryGetValue(workspaceId, out var text) ? text : WorkspaceGuideText.Empty;
    }

    public async Task<IReadOnlyDictionary<long, WorkspaceGuideText>> ListAsync(CancellationToken ct)
    {
        await EnsureLoadedAsync(ct);
        return new Dictionary<long, WorkspaceGuideText>(_texts);
    }

    public async Task SaveAsync(long workspaceId, string summary, string instructions, string changedBy, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(changedBy);
        var cleanSummary = WorkspaceGuideRules.NormalizeSummary(summary ?? string.Empty);
        var cleanInstructions = GuideRules.Normalize(instructions ?? string.Empty);
        Validate(cleanSummary, cleanInstructions);

        await EnsureLoadedAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            if (cleanSummary.Length == 0 && cleanInstructions.Length == 0)
            {
                await _store.DeleteAsync(workspaceId, ct);
                _texts.TryRemove(workspaceId, out _);
            }
            else
            {
                var now = _time.GetUtcNow();
                if (!await _store.UpsertAsync(workspaceId, cleanSummary, cleanInstructions, changedBy, now, ct))
                {
                    _texts.TryRemove(workspaceId, out _);
                    throw new KeyNotFoundException("The workspace no longer exists.");
                }

                _texts[workspaceId] = new WorkspaceGuideText(cleanSummary, cleanInstructions, now, changedBy);
            }
        }
        finally
        {
            _gate.Release();
        }

        // Outside the gate: a page that listens reads the text back at once. An open Instructions page must show text that an import wrote.
        await _events.PublishAsync(new WorkspaceGuideChanged(workspaceId));
    }

    public void Forget(long workspaceId) => _texts.TryRemove(workspaceId, out _);

    public void Dispose()
    {
        _subscription.Dispose();
        _gate.Dispose();
    }

    private static void Validate(string summary, string instructions)
    {
        if (summary.Length > WorkspaceGuideRules.MaxSummaryChars)
        {
            throw new ArgumentException(
                $"The summary is {summary.Length:N0} characters; the limit is {WorkspaceGuideRules.MaxSummaryChars:N0}. Every agent reads it in the overview, so keep it to one short line.");
        }

        if (instructions.Length > WorkspaceGuideRules.MaxInstructionsChars)
        {
            throw new ArgumentException(
                $"The instructions are {instructions.Length:N0} characters; the limit is {WorkspaceGuideRules.MaxInstructionsChars:N0}. Every agent that works in this workspace receives all of them, so keep them focused.");
        }

        if (SensitiveDataScanner.Redact(summary).HasFindings || SensitiveDataScanner.Redact(instructions).HasFindings)
        {
            throw new ArgumentException("The text looks like it contains a secret. Every agent that works in this workspace receives it, so remove the secret.");
        }
    }

    private async Task EnsureLoadedAsync(CancellationToken ct)
    {
        if (_loaded)
        {
            return;
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (_loaded)
            {
                return;
            }

            foreach (var row in await _store.ListAsync(ct))
            {
                _texts.TryAdd(row.WorkspaceId, new WorkspaceGuideText(row.Summary, row.Instructions, row.ChangedUtc, row.ChangedBy));
            }

            _loaded = true;
        }
        finally
        {
            _gate.Release();
        }
    }
}
