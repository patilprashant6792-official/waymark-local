using System.Threading.Channels;
using Waymark.Core.Commands;
using Waymark.Core.Events;
using Waymark.Host.Edge;

namespace Waymark.Host.Notifications;

/// <summary>What one nudge did; for the tests and the log.</summary>
internal enum NudgeOutcome
{
    Shown,
    Disabled,
    NothingWaiting,
    Failed,
}

/// <summary>
/// Tells the owner on the desktop that a command or an app connection waits for approval, so it is noticed from any app. It only
/// points to the admin UI: approving and denying stay there, through the same calls as before.
/// <para>
/// A request only sets a signal, so the code that published the event never waits for a notification. The loop then waits a short
/// quiet period so a burst of requests becomes one notification, keeps at least <see cref="Cooldown"/> between two notifications,
/// and counts what still waits just before it shows anything: a request decided in the meantime shows nothing. A notification that
/// fails is logged and the next request tries again.
/// </para>
/// </summary>
public sealed class ApprovalNudgeService : BackgroundService
{
    internal static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(30);

    // One slot: any number of requests before the loop wakes up are one signal.
    private readonly Channel<bool> _signals = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true, SingleWriter = false });

    private readonly IPendingDecisionSource _pending;
    private readonly IDesktopNotificationSettings _settings;
    private readonly IDesktopNotifier _notifier;
    private readonly EdgeOptions _edge;
    private readonly TimeProvider _time;
    private readonly ILogger<ApprovalNudgeService> _logger;
    private readonly List<IDisposable> _subscriptions = [];
    private DateTimeOffset? _lastAttempt;

    public ApprovalNudgeService(
        IEventBus events,
        IPendingDecisionSource pending,
        IDesktopNotificationSettings settings,
        IDesktopNotifier notifier,
        EdgeOptions edge,
        TimeProvider time,
        ILogger<ApprovalNudgeService> logger)
    {
        ArgumentNullException.ThrowIfNull(events);
        _pending = pending;
        _settings = settings;
        _notifier = notifier;
        _edge = edge;
        _time = time;
        _logger = logger;

        // Subscribed here rather than in ExecuteAsync, so a request made while the host is still starting is not missed.
        _subscriptions.Add(events.Subscribe<CommandRunChanged>(OnCommandRunChanged));
        _subscriptions.Add(events.Subscribe<ApprovalRequested>(_ => Signal()));
    }

    /// <summary>True when a request arrived that no notification has covered yet.</summary>
    internal bool HasSignal => _signals.Reader.Count > 0;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_notifier.IsSupported)
        {
            _logger.LogDebug("Desktop notifications are not available on this system; the admin UI still shows what waits");
            return;
        }

        var reader = _signals.Reader;
        try
        {
            while (await reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
            {
                Drain();
                await Task.Delay(QuietPeriod, _time, stoppingToken).ConfigureAwait(false);

                var wait = WaitBeforeNudge(_time.GetUtcNow(), _lastAttempt, Cooldown);
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, _time, stoppingToken).ConfigureAwait(false);
                }

                // Requests that came during the waits are covered by this notification: it counts everything that waits now.
                Drain();
                await NudgeAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Waymark is stopping.
        }
    }

    /// <summary>Counts what waits and shows one notification for it. Never throws, except when <paramref name="ct"/> is cancelled.</summary>
    internal async Task<NudgeOutcome> NudgeAsync(CancellationToken ct)
    {
        try
        {
            if (!await _settings.IsEnabledAsync(ct).ConfigureAwait(false))
            {
                return NudgeOutcome.Disabled;
            }

            var pending = await _pending.CountAsync(ct).ConfigureAwait(false);
            var notice = ApprovalNoticeText.For(pending, _edge.AdminPort);
            if (notice is null)
            {
                return NudgeOutcome.NothingWaiting;
            }

            // Set before showing, so a notifier that keeps failing is not started again for every request.
            _lastAttempt = _time.GetUtcNow();
            await _notifier.ShowAsync(notice, ct).ConfigureAwait(false);
            _logger.LogDebug("Desktop notification shown: {Commands} command(s) and {Pairings} app connection(s) waiting", pending.Commands, pending.Pairings);
            return NudgeOutcome.Shown;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The desktop notification about a waiting approval could not be shown; the admin UI still lists it");
            return NudgeOutcome.Failed;
        }
    }

    /// <summary>How long to wait so two notifications are at least <paramref name="cooldown"/> apart. Zero when none was shown yet.</summary>
    internal static TimeSpan WaitBeforeNudge(DateTimeOffset now, DateTimeOffset? lastAttempt, TimeSpan cooldown)
    {
        if (lastAttempt is null)
        {
            return TimeSpan.Zero;
        }

        var wait = lastAttempt.Value + cooldown - now;
        return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
    }

    public override void Dispose()
    {
        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }

        _subscriptions.Clear();
        base.Dispose();
        GC.SuppressFinalize(this);
    }

    private ValueTask OnCommandRunChanged(CommandRunChanged change)
    {
        // Only a new request that asks the owner; queued, running and finished runs need nothing from them.
        return change.Status == CommandRunStatus.WaitingApproval ? Signal() : ValueTask.CompletedTask;
    }

    private ValueTask Signal()
    {
        _signals.Writer.TryWrite(true);
        return ValueTask.CompletedTask;
    }

    private void Drain()
    {
        while (_signals.Reader.TryRead(out _))
        {
        }
    }
}
