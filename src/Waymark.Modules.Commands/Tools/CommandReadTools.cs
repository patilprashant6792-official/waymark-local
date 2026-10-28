using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Waymark.Modules.Commands.Tools;

/// <summary>Tools to follow commands that were started: wait for them, read their output, list them, stop them. They cannot approve anything.</summary>
[McpServerToolType]
public sealed class CommandReadTools(CommandService service)
{
    [McpServerTool(Name = "cmd_wait", ReadOnly = true, Idempotent = true)]
    [Description(
        "Wait for a command (run id from cmd_project, cmd_git, cmd_proc or cmd_shell) to finish or to be decided by the owner, then report it like the original call did. " +
        "Returns early when the run ends. Call it again when the run is still going.")]
    public Task<string> Wait(
        [Description("The run number, for example 12.")] long runId,
        [Description("Seconds to wait (default 30, at most about 45).")] int? waitSeconds = null,
        CancellationToken cancellationToken = default) =>
        service.WaitAsync(runId, waitSeconds, cancellationToken);

    [McpServerTool(Name = "cmd_output", ReadOnly = true, Idempotent = true)]
    [Description(
        "Read the output of a command as plain text (no colours), with line numbers. Without options you get the last lines. " +
        "Use grep to find lines (case-insensitive text) and fromLine/lines to page through a long output.")]
    public Task<string> Output(
        [Description("The run number.")] long runId,
        [Description("First line to show (1-based). Omit to show the last lines.")] int? fromLine = null,
        [Description("How many lines (default 150, or 100 with grep; at most 500).")] int? lines = null,
        [Description("Only show lines that contain this text (case-insensitive).")] string? grep = null,
        CancellationToken cancellationToken = default) =>
        service.OutputAsync(runId, fromLine, lines, grep, cancellationToken);

    [McpServerTool(Name = "cmd_list", ReadOnly = true, Idempotent = true)]
    [Description("List the most recent commands (newest first) with their run numbers and status, optionally for one workspace.")]
    public Task<string> List(
        [Description("Optional workspace name.")] string? workspace = null,
        [Description("How many runs (default 10, at most 30).")] int? limit = null,
        CancellationToken cancellationToken = default) =>
        service.ListAsync(workspace, limit, cancellationToken);

    [McpServerTool(Name = "cmd_stop", ReadOnly = false, Destructive = false, Idempotent = true)]
    [Description("End a command that waits, is queued or runs (for example a dotnet run). The whole process tree is ended. Does nothing when the run already finished.")]
    public Task<string> Stop(
        [Description("The run number.")] long runId,
        CancellationToken cancellationToken = default) =>
        service.StopAsync(runId, cancellationToken);
}
