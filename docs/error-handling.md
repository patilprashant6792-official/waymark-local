# Error handling

Rule: a failure is reported and skipped; the service keeps running. Every failure that is caught is logged as an error, and the Live page shows it under Errors (and on All). Nothing is shown to the person using the admin UI except a short notice where a page could not be drawn.

## Layers, from the inside out

| Where | What happens on a failure |
|---|---|
| A tool call (`ToolPipeline`) | The call returns a failure result to Claude, one audit row is written, the Tools tab shows an error line |
| A command run (`CommandCoordinator`) | The run ends as failed with a note; the Commands tab shows an error line |
| A handler of an event (`EventBus`) | Logged as an error; the other handlers still run |
| A web request | ASP.NET answers 500 (the admin UI shows its error page, the tunnel listener a small JSON body) and logs it |
| A page that fails while it draws (`PageErrorBoundary` in `MainLayout`) | The page is replaced by a notice with a "try this page again" button; the layout and the circuit stay; going to another page starts clean |
| A background service (`HostOptions.BackgroundServiceExceptionBehavior = Ignore`) | The exception is logged and the host keeps running. A service that throws out of its main method stays stopped until Waymark restarts, so the services below catch their own failures inside their loops |
| A callback on a thread nobody awaits (file watcher events, output of a child process) | Caught in place and sent to `FailureReporter`, which logs an error and drops that one event or line |
| A task that failed with nobody waiting (`TaskScheduler.UnobservedTaskException`) | Logged as an error and marked as seen (`GlobalSafetyNet`) |
| Start-up chores that only refresh or tidy (public address, expired codes) | Reported and skipped |

## What is deliberately not caught

- **The database at start-up.** Without it nothing works, and a migration that fails must stop the app (`docs/migration/smoke.md`).
- **Starting the web server** (for example a port already in use).
- **Exceptions the runtime itself does not allow to be stopped:** an exception that nothing caught on a thread of its own, a stack overflow, running out of memory. `GlobalSafetyNet` writes a critical line first, but the process ends. The code that runs on such threads catches its own failures for that reason.

## Background services

| Service | If its start fails | If one round fails |
|---|---|---|
| `NgrokTunnelHostedService` | n/a | logged, retried with a growing pause |
| `AuditRetentionService` | a bad `Waymark:Audit:RetentionDays` value stops it (logged once) | logged, tried again after six hours |
| `CommandMaintenanceService` | logged | logged, tried again after six hours |
| `IndexScheduler` | the listing of workspaces is logged; the worker keeps running | one workspace's work is logged, the next is processed |
| `FileWatcherHost` | each workspace is started on its own; one that cannot be watched is logged | the event is dropped and logged |

## Adding code

- On a thread nobody awaits (a timer, a `FileSystemWatcher` or `Process` event, a fire-and-forget task): wrap the body in `try`/`catch (Exception ex)` and call `FailureReporter.Report(ex, "what was being done")`.
- In a loop of a background service: catch inside the loop, log, wait, go on.
- Do not swallow silently: a failure without a log line is invisible.
