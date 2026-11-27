using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Waymark.Modules.Commands.Tools;

/// <summary>Tools to follow commands that were started: wait for them, read their output, list them, stop them. They cannot approve anything.</summary>
[McpServerToolType]
public sealed class CommandReadTools(CommandService service)
{
    [McpServerTool(Name = "cmd_wait", ReadOnly = true, Idempotent = true)]
    [Description(
        "Waits for one command run to finish, or for the owner to approve or deny it, then returns the same report as the call that started it: status, exit code, duration and the last output lines. " +
        "It returns as soon as the run ends; when waitSeconds runs out first it reports the current state (waiting for approval, queued or running) and you call it again. Waiting changes nothing. " +
        "A run in a terminal (an app, a dev server, a watcher) counts as done waiting once its program is up: it never ends by itself, so do not wait for that.\n" +
        "WHEN TO USE: cmd_project, cmd_git, cmd_proc or cmd_shell gave you a run id instead of a result, because the run waits for approval or takes longer than its waitSeconds. " +
        "Not for reading more output now (cmd_output), ending a run (cmd_stop) or finding a lost run id (cmd_list). An unknown run id is an error.")]
    public Task<string> Wait(
        [Description("The run number the starting call returned, for example 12.")] long runId,
        [Description("Seconds to wait before reporting the current state (default 30, at most about 45, which keeps the call inside the client's time limit).")] int? waitSeconds = null,
        CancellationToken cancellationToken = default) =>
        service.WaitAsync(runId, waitSeconds, cancellationToken);

    [McpServerTool(Name = "cmd_output", ReadOnly = true, Idempotent = true)]
    [Description(
        "Reads the output of one command run as plain text with line numbers (colours removed), finished or still running. Three ways: " +
        "no options = the last 150 lines; fromLine (+ lines) = a window starting at that line; grep = only the lines that contain that text, with their line numbers (100 by default). " +
        "The header gives the run's status and the total line count, so you can page with fromLine; at most 500 lines per call, and a very long reply is cut to the size limit. " +
        "A run that is still going says so: call again for newer lines.\n" +
        "WHEN TO USE: you need more than the last lines a cmd_* result or cmd_wait showed, such as the first compiler error or a whole test failure. " +
        "To wait for the run to end use cmd_wait; to find a run id use cmd_list. An unknown run id is an error.")]
    public Task<string> Output(
        [Description("The run number from a cmd_* result or cmd_list, for example 12.")] long runId,
        [Description("Optional: first line to show (1-based). Omit for the last lines. Ignored with grep.")] int? fromLine = null,
        [Description("Optional: how many lines (default 150, or 100 with grep; 1 to 500).")] int? lines = null,
        [Description("Optional: plain text, not a pattern; only lines that contain it are shown, any letter case. Example: 'error CS', 'FAILED'.")] string? grep = null,
        CancellationToken cancellationToken = default) =>
        service.OutputAsync(runId, fromLine, lines, grep, cancellationToken);

    [McpServerTool(Name = "cmd_list", ReadOnly = true, Idempotent = true)]
    [Description(
        "Lists the most recent command runs, newest first. Each row: run number, status (waiting for approval, queued, running, succeeded, failed, timed out, cancelled or denied), " +
        "operation, workspace, the command line (cut at 90 characters) and how long ago it was requested. Reads only.\n" +
        "WHEN TO USE: to find a run id you no longer have, to see what is still running before you start another build or app, or to check whether the owner approved or denied a request. " +
        "Then cmd_output reads a run's output, cmd_wait waits for one that is still going and cmd_stop ends one. Not for output itself: rows carry no output.")]
    public Task<string> List(
        [Description("Optional: only runs of this workspace (name as shown in Waymark). Omit for runs of every workspace and system scripts.")] string? workspace = null,
        [Description("Optional: how many runs to show (default 10, 1 to 30).")] int? limit = null,
        CancellationToken cancellationToken = default) =>
        service.ListAsync(workspace, limit, cancellationToken);

    [McpServerTool(Name = "cmd_stop", ReadOnly = false, Destructive = true, Idempotent = true)]
    [Description(
        "Ends one command run. A run that still waits for approval or is queued is withdrawn and never starts. " +
        "A program running in a terminal is stopped as a developer stops it: Ctrl+C, then a request to terminate, then a kill of everything it started; any other run is killed at once with its processes. " +
        "It then waits a few seconds and reports the final state like cmd_wait. A run that already finished is left as it is and the reply gives its status; an unknown run id is an error.\n" +
        "WHEN TO USE: to end a hung build or test, a request you no longer want, or an app in a terminal (cmd_terminal action stop does the same by terminal name). " +
        "For a program Waymark did not start that holds a port use cmd_proc stop_port; to find run ids use cmd_list.")]
    public Task<string> Stop(
        [Description("The run number to end, from a cmd_* result or cmd_list, for example 12.")] long runId,
        CancellationToken cancellationToken = default) =>
        service.StopAsync(runId, cancellationToken);
}
