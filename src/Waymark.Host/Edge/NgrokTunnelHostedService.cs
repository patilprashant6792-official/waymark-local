using Waymark.Core.Edge;

namespace Waymark.Host.Edge;

/// <summary>
/// Keeps the tunnel up while Waymark runs: brings it online at startup when an address and authtoken are saved,
/// notices when it drops, and retries with a growing pause. Installing ngrok is never done here, only on an explicit save.
/// </summary>
public sealed class NgrokTunnelHostedService(
    NgrokTunnel tunnel,
    NgrokLocator locator,
    INgrokAuthStore auth,
    IPublicUrlProvider urls,
    TimeProvider time,
    ILogger<NgrokTunnelHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);
    private const int MaxBackoffShift = 5;

    private int _failures;
    private DateTimeOffset _nextAttempt = DateTimeOffset.MinValue;

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await tunnel.StopAsync();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckOnceAsync(stoppingToken);
                await Task.Delay(CheckInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "The tunnel check failed unexpectedly; it will be tried again.");
                RecordFailure();
                await DelayQuietlyAsync(stoppingToken);
            }
        }
    }

    private async Task CheckOnceAsync(CancellationToken ct)
    {
        if (urls.Current is not { } origin || locator.Find() is null || !(await auth.GetInfoAsync(ct)).IsSet)
        {
            return;
        }

        if (tunnel.IsHeld || tunnel.Status.IsBusy || time.GetUtcNow() < _nextAttempt)
        {
            return;
        }

        var status = await tunnel.EnsureAsync(origin, allowInstall: false, ct);
        if (status.IsOnline)
        {
            _failures = 0;
            _nextAttempt = DateTimeOffset.MinValue;
            return;
        }

        RecordFailure();
    }

    private void RecordFailure()
    {
        _failures++;
        var pause = TimeSpan.FromTicks(CheckInterval.Ticks << Math.Min(_failures, MaxBackoffShift));
        _nextAttempt = time.GetUtcNow() + (pause > MaxBackoff ? MaxBackoff : pause);
    }

    private static async Task DelayQuietlyAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(CheckInterval, ct);
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }
}
