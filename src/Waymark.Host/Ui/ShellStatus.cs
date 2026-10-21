using System.Text.Json;
using Waymark.Core.Approvals;
using Waymark.Core.Audit;
using Waymark.Core.Auth;
using Waymark.Core.Edge;
using Waymark.Core.Commands;
using Waymark.Core.Events;
using Waymark.Core.LiveFeed;
using Waymark.Core.Settings;
using Waymark.Host.Edge;

namespace Waymark.Host.Ui;

/// <summary>
/// What the header, the navigation and the Home tiles show: the pause switch, the tunnel, the public address and the number of
/// pairings waiting. One shared instance; it follows the event bus and every open page redraws on <see cref="Changed"/>.
/// </summary>
public sealed class ShellStatus : IDisposable
{
    private readonly IConfigStore _config;
    private readonly IApprovalStore _approvals;
    private readonly IPublicUrlProvider _publicUrl;
    private readonly NgrokTunnel _tunnel;
    private readonly IAuditStore _audit;
    private readonly TimeProvider _time;
    private readonly ILogger<ShellStatus> _logger;
    private readonly ICommandRuns _commands;
    private readonly LiveActivity _activity;
    private readonly List<IDisposable> _subscriptions = [];
    private volatile bool _paused;
    private int _pendingApprovals;
    private int _pendingCommands;

    public ShellStatus(
        IConfigStore config,
        IApprovalStore approvals,
        IPublicUrlProvider publicUrl,
        NgrokTunnel tunnel,
        IAuditStore audit,
        IEventBus events,
        ICommandRuns commands,
        LiveActivity activity,
        TimeProvider time,
        ILogger<ShellStatus> logger)
    {
        _config = config;
        _approvals = approvals;
        _publicUrl = publicUrl;
        _tunnel = tunnel;
        _audit = audit;
        _time = time;
        _logger = logger;
        _commands = commands;
        _activity = activity;
        _activity.Changed += RaiseChanged;

        _subscriptions.Add(events.Subscribe<ApprovalRequested>(_ => OnEventAsync()));
        _subscriptions.Add(events.Subscribe<ApprovalDecided>(_ => OnEventAsync()));
        _subscriptions.Add(events.Subscribe<TunnelStatusChanged>(_ => OnEventAsync()));
        _subscriptions.Add(events.Subscribe<PublicUrlChanged>(_ => OnEventAsync()));
        _subscriptions.Add(events.Subscribe<CommandRunChanged>(_ => OnEventAsync()));
    }

    /// <summary>Raised after any value changed. Handlers run on a background thread; pages must marshal with InvokeAsync.</summary>
    public event Action? Changed;

    public bool Paused => _paused;

    public int PendingApprovals => Volatile.Read(ref _pendingApprovals);

    /// <summary>Commands Claude asked for that wait for the owner's decision.</summary>
    public int PendingCommands => Volatile.Read(ref _pendingCommands);

    /// <summary>How many tool calls and commands are in flight right now.</summary>
    public int ActiveNow => _activity.Count;

    public TunnelStatus Tunnel => _tunnel.Status;

    public string? PublicUrl => _publicUrl.Current;

    /// <summary>Re-reads the pause switch and the pending count, then raises <see cref="Changed"/>.</summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        try
        {
            _paused = await ReadPausedAsync(ct).ConfigureAwait(false);
            var pending = await _approvals.ListPendingAsync(_time.GetUtcNow(), ct).ConfigureAwait(false);
            Volatile.Write(ref _pendingApprovals, pending.Count);
            var waiting = await _commands.ListPendingAsync(ct).ConfigureAwait(false);
            Volatile.Write(ref _pendingCommands, waiting.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Could not refresh the status shown in the header");
        }

        RaiseChanged();
    }

    public async Task SetPausedAsync(bool paused, CancellationToken ct)
    {
        await _config.SetAsync(SettingScope.Global, SettingKeys.Paused, paused ? "true" : "false", AuthDefaults.AdminActor, ct).ConfigureAwait(false);
        await _audit.AppendAsync(
            AuditEntries.Create(_time, AuditCategory.Config, paused ? "config.paused" : "config.resumed", AuthDefaults.AdminActor, SettingKeys.Paused),
            ct).ConfigureAwait(false);
        _paused = paused;
        RaiseChanged();
    }

    public void Dispose()
    {
        _activity.Changed -= RaiseChanged;
        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }
    }

    // A value that cannot be read counts as paused, like the call pipeline does.
    private async Task<bool> ReadPausedAsync(CancellationToken ct)
    {
        var setting = await _config.GetAsync(SettingScope.Global, SettingKeys.Paused, ct).ConfigureAwait(false);
        if (setting is null)
        {
            return false;
        }

        try
        {
            return JsonSerializer.Deserialize<bool>(setting.ValueJson);
        }
        catch (JsonException)
        {
            return true;
        }
    }

    private async ValueTask OnEventAsync() => await RefreshAsync(CancellationToken.None).ConfigureAwait(false);

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A page failed while redrawing after a status change");
        }
    }
}
