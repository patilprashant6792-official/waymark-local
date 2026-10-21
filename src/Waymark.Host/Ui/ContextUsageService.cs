using System.ComponentModel;
using System.Diagnostics;
using Waymark.Core.Audit;

namespace Waymark.Host.Ui;

/// <param name="Session">Since this copy of Waymark started.</param>
/// <param name="Today">Since midnight, your time.</param>
/// <param name="Recorded">Everything the audit trail still holds.</param>
/// <param name="TopToolsToday">The tools that sent the most to the AI today, most first.</param>
public sealed record ContextUsageSnapshot(ToolUsage Session, ToolUsage Today, ToolUsage Recorded, IReadOnlyList<ToolUsageByTool> TopToolsToday);

/// <summary>Totals of the estimated tokens that tool calls sent to the AI, read from the audit trail.</summary>
public sealed class ContextUsageService(IAuditStore audit, TimeProvider time)
{
    private const int TopToolCount = 3;

    private static readonly DateTimeOffset StartedUtc = ReadProcessStart();

    public async Task<ContextUsageSnapshot> GetAsync(CancellationToken ct)
    {
        var now = time.GetLocalNow();
        var midnight = new DateTimeOffset(now.Date, time.LocalTimeZone.GetUtcOffset(now.Date));

        var session = await audit.UsageAsync(StartedUtc, ct);
        var today = await audit.UsageAsync(midnight, ct);
        var recorded = await audit.UsageAsync(null, ct);
        var top = await audit.UsageByToolAsync(midnight, TopToolCount, ct);
        return new ContextUsageSnapshot(session, today, recorded, top);
    }

    private static DateTimeOffset ReadProcessStart()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return new DateTimeOffset(process.StartTime.ToUniversalTime());
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Without the start time, the session starts now.
            return DateTimeOffset.UtcNow;
        }
    }
}
