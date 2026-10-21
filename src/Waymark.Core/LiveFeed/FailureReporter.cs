using Microsoft.Extensions.Logging;

namespace Waymark.Core.LiveFeed;

/// <summary>
/// The last place a failure goes when the code that met it cannot do anything useful: a callback on a thread nobody awaits, an output handler
/// of a child process. The failure is skipped, logged as an error (the Live page shows it under Errors) and the work goes on.
/// The host installs the logger once at start-up; until then reporting does nothing.
/// </summary>
public static class FailureReporter
{
    private static ILogger? _logger;

    public static void Install(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        Volatile.Write(ref _logger, logger);
    }

    /// <param name="what">What was being done, in a few plain words ("Reading the output of a helper program").</param>
    public static void Report(Exception exception, string what)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentException.ThrowIfNullOrWhiteSpace(what);
        try
        {
            Volatile.Read(ref _logger)?.LogError(exception, "{What} failed and was skipped", what);
        }
        catch (Exception)
        {
            // Reporting a failure must never cause another one.
        }
    }
}
