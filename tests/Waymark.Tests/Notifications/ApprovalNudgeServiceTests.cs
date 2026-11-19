using Microsoft.Extensions.Logging;
using Waymark.Core.Commands;
using Waymark.Core.Events;
using Waymark.Host.Edge;
using Waymark.Host.Notifications;
using Waymark.Tests.Support;

namespace Waymark.Tests.Notifications;

public sealed class ApprovalNudgeServiceTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 19, 6, 0, 0, TimeSpan.Zero);

    private readonly EventBus _bus = new(new CapturingLogger<EventBus>());
    private readonly FakePendingDecisions _pending = new();
    private readonly FakeNotificationSettings _settings = new();
    private readonly FakeNotifier _notifier = new();
    private readonly Clock _clock = new(Start);
    private readonly CapturingLogger<ApprovalNudgeService> _logger = new();
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly ApprovalNudgeService _service;

    public ApprovalNudgeServiceTests()
    {
        _service = new ApprovalNudgeService(_bus, _pending, _settings, _notifier, new EdgeOptions(47100, 47101), _clock, _logger);
    }

    public void Dispose() => _service.Dispose();

    [Fact]
    public async Task ACommandThatWaitsForApprovalSetsTheSignal()
    {
        await _bus.PublishAsync(new CommandRunChanged(1, 3, CommandRunStatus.WaitingApproval));

        Assert.True(_service.HasSignal);
    }

    [Theory]
    [InlineData(CommandRunStatus.Queued)]
    [InlineData(CommandRunStatus.Running)]
    [InlineData(CommandRunStatus.Denied)]
    public async Task OtherCommandStatusesDoNotSetTheSignal(CommandRunStatus status)
    {
        await _bus.PublishAsync(new CommandRunChanged(1, 3, status));

        Assert.False(_service.HasSignal);
    }

    [Fact]
    public async Task APairingRequestSetsTheSignal()
    {
        await _bus.PublishAsync(new ApprovalRequested(9));

        Assert.True(_service.HasSignal);
    }

    [Fact]
    public async Task NoSignalAfterTheServiceIsDisposed()
    {
        _service.Dispose();

        await _bus.PublishAsync(new ApprovalRequested(9));

        Assert.False(_service.HasSignal);
    }

    [Fact]
    public async Task ShowsOneNoticeWithTheCountsAndTheConsoleAddress()
    {
        _pending.Now = new PendingDecisions(2, 1);

        var outcome = await _service.NudgeAsync(_ct);

        Assert.Equal(NudgeOutcome.Shown, outcome);
        var notice = Assert.Single(_notifier.Shown);
        Assert.Contains("2 commands and 1 app connection are waiting", notice.Message, StringComparison.Ordinal);
        Assert.Equal("http://localhost:47100/console", notice.OpenUri.AbsoluteUri);
    }

    [Fact]
    public async Task ShowsNothingWhenEverythingWasDecidedMeanwhile()
    {
        _pending.Now = new PendingDecisions(0, 0);

        var outcome = await _service.NudgeAsync(_ct);

        Assert.Equal(NudgeOutcome.NothingWaiting, outcome);
        Assert.Empty(_notifier.Shown);
    }

    [Fact]
    public async Task ShowsNothingWhenSwitchedOff()
    {
        _settings.Enabled = false;
        _pending.Now = new PendingDecisions(1, 0);

        var outcome = await _service.NudgeAsync(_ct);

        Assert.Equal(NudgeOutcome.Disabled, outcome);
        Assert.Empty(_notifier.Shown);
    }

    [Fact]
    public async Task AFailingNotifierIsLoggedAndDoesNotThrow()
    {
        _notifier.FailWith = new InvalidOperationException("powershell.exe ended with exit code 1");
        _pending.Now = new PendingDecisions(1, 0);

        var outcome = await _service.NudgeAsync(_ct);

        Assert.Equal(NudgeOutcome.Failed, outcome);
        Assert.Contains(_logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Exception == _notifier.FailWith);
    }

    [Fact]
    public async Task CancellationIsNotSwallowed()
    {
        _notifier.FailWith = new OperationCanceledException();
        _pending.Now = new PendingDecisions(1, 0);
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.NudgeAsync(cancel.Token));
    }

    [Fact]
    public void NoWaitBeforeTheFirstNotification()
    {
        Assert.Equal(TimeSpan.Zero, ApprovalNudgeService.WaitBeforeNudge(Start, null, TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void WaitsForTheRestOfTheCooldown()
    {
        var wait = ApprovalNudgeService.WaitBeforeNudge(Start.AddSeconds(10), Start, TimeSpan.FromSeconds(30));

        Assert.Equal(TimeSpan.FromSeconds(20), wait);
    }

    [Fact]
    public void NoWaitOnceTheCooldownHasPassed()
    {
        Assert.Equal(TimeSpan.Zero, ApprovalNudgeService.WaitBeforeNudge(Start.AddMinutes(5), Start, TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task ABurstOfRequestsBecomesOneNotification()
    {
        _pending.Now = new PendingDecisions(3, 0);
        var run = new ApprovalNudgeService(_bus, _pending, _settings, _notifier, new EdgeOptions(47100, 47101), TimeProvider.System, _logger);
        await run.StartAsync(_ct);
        try
        {
            for (var id = 1; id <= 3; id++)
            {
                await _bus.PublishAsync(new CommandRunChanged(id, 3, CommandRunStatus.WaitingApproval));
            }

            // The quiet period is two seconds; give it room on a slow test machine.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (_notifier.Shown.Count == 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(100, _ct);
            }

            await Task.Delay(500, _ct);
            var notice = Assert.Single(_notifier.Shown);
            Assert.Contains("3 commands are waiting", notice.Message, StringComparison.Ordinal);
        }
        finally
        {
            await run.StopAsync(_ct);
            run.Dispose();
        }
    }
}
