using System.Collections.Concurrent;
using System.Globalization;
using ModelContextProtocol.Protocol;
using Waymark.Core.Commands;
using Waymark.Core.Events;
using Waymark.Core.Security;
using Waymark.Core.Workspaces;

namespace Waymark.Host.Mcp;

/// <summary>
/// Brings what happened in terminals to Claude where it works, without it asking: after every tool call, at most one 🖥 line about a terminal event
/// this client was not told about yet (a crash, the owner stopping a terminal, an app going down inside its watcher, a prompt it waits at, new
/// errors). Events of the workspace the call works in and of the system terminals count; a crash or an owner's stop counts wherever it happened.
/// A client is told about events from its first call on, not about what happened before it connected (its guide shows the terminals' state).
/// An event is checked against the terminal as it is now before it is told: a closed terminal, an answered prompt, errors Claude has read since,
/// an app that is up again, are not news any more and are left out.
/// The line is Waymark's own text and is added after the result's secret check, so it passes the secret scanner itself.
/// </summary>
public sealed class TerminalNotices : IDisposable
{
    private const int MaxKept = 200;
    private readonly object _gate = new();
    private readonly List<TerminalNotice> _recent = [];
    private readonly ConcurrentDictionary<string, long> _told = new(StringComparer.OrdinalIgnoreCase);
    private readonly IWorkspaceService _workspaces;
    private readonly ITerminals? _terminals;
    private readonly ILogger<TerminalNotices> _logger;
    private readonly IDisposable _subscription;
    private long _latest;

    public TerminalNotices(IEventBus events, IWorkspaceService workspaces, ILogger<TerminalNotices> logger, ITerminals? terminals = null)
    {
        ArgumentNullException.ThrowIfNull(events);
        _workspaces = workspaces ?? throw new ArgumentNullException(nameof(workspaces));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _terminals = terminals;
        _subscription = events.Subscribe<TerminalNotice>(OnNotice);
    }

    /// <summary>Adds at most one 🖥 line to the result. Returns the text added, or null.</summary>
    public async Task<string?> AfterCallAsync(CallToolResult result, string client, string? workspaceName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(result);
        try
        {
            var key = string.IsNullOrWhiteSpace(client) ? "unknown" : client;
            var latest = Interlocked.Read(ref _latest);
            if (!_told.TryGetValue(key, out var seen))
            {
                _told[key] = latest;
                return null;
            }

            List<TerminalNotice> fresh;
            lock (_gate)
            {
                fresh = _recent.Where(n => n.Seq > seen).ToList();
            }

            if (fresh.Count == 0)
            {
                return null;
            }

            _told[key] = fresh.Max(n => n.Seq);
            var all = await _workspaces.ListAsync(ct);
            var here = workspaceName is null ? null : all.FirstOrDefault(w => string.Equals(w.Name, workspaceName, StringComparison.OrdinalIgnoreCase))?.Id;
            var relevant = new List<TerminalNotice>();
            var now = new Dictionary<long, TerminalInfo?>();
            foreach (var notice in fresh
                .Where(n => n.WorkspaceId is null || n.WorkspaceId == here || n.Kind is TerminalNoticeKind.Crashed or TerminalNoticeKind.StoppedByOwner)
                .OrderBy(n => Priority(n.Kind))
                .ThenByDescending(n => n.Seq))
            {
                if (await StillNewsAsync(notice, now, ct))
                {
                    relevant.Add(notice);
                }
            }

            if (relevant.Count == 0)
            {
                return null;
            }

            var top = relevant[0];
            var scope = top.WorkspaceId is { } id && all.FirstOrDefault(w => w.Id == id) is { } workspace ? $", workspace='{workspace.Name}'" : string.Empty;
            var hint = top.Kind == TerminalNoticeKind.StoppedByOwner
                ? string.Empty
                : top.Kind == TerminalNoticeKind.WaitsForInput
                    ? $" → cmd_terminal(action='send', name='{top.TerminalName}'{scope}, optionsJson='{{\"text\":\"...\"}}')"
                    : $" → cmd_terminal(action='read', name='{top.TerminalName}'{scope})";
            var more = relevant.Count > 1
                ? string.Create(CultureInfo.InvariantCulture, $" (+{relevant.Count - 1} more terminal event(s): cmd_terminal(action='list', workspace='*'))")
                : string.Empty;
            var line = SensitiveDataScanner.Redact($"🖥 {top.Text}{hint}{more}").Text;
            result.Content.Add(new TextContentBlock { Text = line });
            return line;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "The terminal line for a tool result was left out");
            return null;
        }
    }

    public void Dispose() => _subscription.Dispose();

    // What the terminal is now decides whether the event still matters. Without the terminals service every event is told as it came.
    private async Task<bool> StillNewsAsync(TerminalNotice notice, Dictionary<long, TerminalInfo?> now, CancellationToken ct)
    {
        if (_terminals is null)
        {
            return true;
        }

        if (!now.TryGetValue(notice.TerminalId, out var info))
        {
            info = await _terminals.GetAsync(notice.TerminalId, ct);
            now[notice.TerminalId] = info;
        }

        if (info is null)
        {
            return false;
        }

        return notice.Kind switch
        {
            TerminalNoticeKind.WaitsForInput => info.PendingPrompt is not null,
            TerminalNoticeKind.NewErrors => info.UnreadErrors > 0 && info.Terminal.State.IsLive(),
            TerminalNoticeKind.AppDown => info.Terminal.State == TerminalState.AppDown,
            _ => true,
        };
    }

    private static int Priority(TerminalNoticeKind kind) => kind switch
    {
        TerminalNoticeKind.Crashed => 0,
        TerminalNoticeKind.StoppedByOwner => 1,
        TerminalNoticeKind.AppDown => 2,
        TerminalNoticeKind.WaitsForInput => 3,
        TerminalNoticeKind.NewErrors => 4,
        _ => 5,
    };

    private ValueTask OnNotice(TerminalNotice notice)
    {
        lock (_gate)
        {
            _recent.Add(notice);
            if (_recent.Count > MaxKept)
            {
                _recent.RemoveRange(0, _recent.Count - MaxKept);
            }

            if (notice.Seq > _latest)
            {
                Interlocked.Exchange(ref _latest, notice.Seq);
            }
        }

        return ValueTask.CompletedTask;
    }
}
