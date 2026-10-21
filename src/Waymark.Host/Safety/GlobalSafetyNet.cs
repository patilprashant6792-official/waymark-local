using Waymark.Core.LiveFeed;

namespace Waymark.Host.Safety;

/// <summary>
/// The process-wide net under everything else. A task that failed with nobody waiting for it is logged and marked as seen. An exception
/// that nothing caught is logged as critical before the runtime ends the process: .NET does not allow stopping that one, so the code
/// that runs on threads nobody awaits catches its own failures (see <see cref="FailureReporter"/>).
/// </summary>
public static class GlobalSafetyNet
{
    private static int _installed;

    public static void Install(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        if (Interlocked.Exchange(ref _installed, 1) == 1)
        {
            return;
        }

        FailureReporter.Install(logger);

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            logger.LogError(e.Exception, "A background task failed and nobody was waiting for its result");
            e.SetObserved();
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            logger.LogCritical(e.ExceptionObject as Exception, "An unhandled exception is ending Waymark (terminating: {IsTerminating}): {Detail}", e.IsTerminating, e.ExceptionObject);
    }
}
