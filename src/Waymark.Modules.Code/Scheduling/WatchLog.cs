using System.Globalization;
using Microsoft.Extensions.Logging;
using Waymark.Core.LiveFeed;

namespace Waymark.Modules.Code.Scheduling;

/// <summary>
/// Shows file changes on the Live page without flooding it: the first few changes of a burst appear one by one, and the rest as one
/// summary line once the burst is over (a branch switch or a build can touch hundreds of files at once).
/// </summary>
internal sealed class WatchLog
{
    private const int LinesPerWindow = 8;
    private const int WindowMs = 4000;

    private readonly Lock _gate = new();
    private long _windowStart;
    private int _lines;
    private int _suppressed;
    private string _summaryWorkspace = string.Empty;

    public void Changed(ILogger logger, string workspace, string path)
    {
        ArgumentNullException.ThrowIfNull(logger);
        var show = false;
        var summarize = false;
        lock (_gate)
        {
            var now = Environment.TickCount64;
            if (now - _windowStart >= WindowMs)
            {
                _windowStart = now;
                _lines = 0;
            }

            if (_lines < LinesPerWindow)
            {
                _lines++;
                show = true;
            }
            else
            {
                summarize = _suppressed == 0;
                _suppressed++;
                _summaryWorkspace = workspace;
            }
        }

        if (show)
        {
            logger.LogWatch(LogLevel.Information, "changed", workspace, path);
        }

        if (summarize)
        {
            _ = SummarizeLaterAsync(logger);
        }
    }

    private async Task SummarizeLaterAsync(ILogger logger)
    {
        await Task.Delay(WindowMs).ConfigureAwait(false);
        int count;
        string workspace;
        lock (_gate)
        {
            count = _suppressed;
            _suppressed = 0;
            workspace = _summaryWorkspace;
        }

        logger.LogWatch(
            LogLevel.Information,
            "burst",
            workspace,
            count == 1 ? "1 more change" : string.Create(CultureInfo.InvariantCulture, $"{count} more changes"));
    }
}
