using Waymark.Core.Audit;
using Waymark.Host.Mcp;

namespace Waymark.Host.Ui;

/// <summary>Deletes audit entries older than the retention period, once at start-up and then every few hours.</summary>
public sealed class AuditRetentionService(IAuditStore audit, ToolPayloadService payloads, IConfiguration configuration, TimeProvider time, ILogger<AuditRetentionService> logger)
    : BackgroundService
{
    public const int DefaultRetentionDays = 90;
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var days = RetentionDays(configuration);
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                var removed = await audit.PruneAsync(time.GetUtcNow() - TimeSpan.FromDays(days), stoppingToken);
                if (removed > 0)
                {
                    logger.LogInformation("Removed {Count} audit entries older than {Days} days", removed, days);
                }

                // The kept inputs and outputs have their own, shorter period (chosen on the Audit page); the audit rows stay.
                var payloadsRemoved = await payloads.PruneAsync(stoppingToken);
                if (payloadsRemoved > 0)
                {
                    logger.LogInformation("Removed the kept input and output of {Count} tool calls older than the chosen period", payloadsRemoved);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Removing old audit entries failed; it will be tried again");
            }
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    public static int RetentionDays(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var text = configuration["Waymark:Audit:RetentionDays"];
        if (string.IsNullOrWhiteSpace(text))
        {
            return DefaultRetentionDays;
        }

        return int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var days) && days is >= 1 and <= 3650
            ? days
            : throw new InvalidOperationException("Waymark:Audit:RetentionDays must be a whole number of days between 1 and 3650.");
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
