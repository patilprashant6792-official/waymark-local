using System.ComponentModel;
using ModelContextProtocol.Server;
using Waymark.Core.Tools;

namespace Waymark.Modules.Commands.Tools;

/// <summary>
/// The terminal tool: follow and drive programs that keep running in named terminals (apps, dev servers, watchers, long scripts). It is a Confirm
/// tool because 'send' types into a program and 'rerun' starts one; the policy gates each of those like any command.
/// </summary>
[McpServerToolType]
public sealed class CommandTerminalTools(CommandService service)
{
    [McpServerTool(Name = "cmd_terminal", ReadOnly = false, Destructive = false, Idempotent = false)]
    [ToolGate(ToolGate.Confirm)]
    [Description(
        "Follow and drive programs that keep running in named terminals, like the terminal tabs of an IDE: an app (cmd_project run, watch), a dev server (node_run), a Python server (py_run) or a script (cmd_shell with terminal). " +
        "A terminal runs one program at a time; the owner watches it live in the Waymark Console and can stop it. Starting one is done by those tools; this one follows it.\n" +
        "ACTIONS (name* = the terminal's name; workspace empty = the computer's system terminals, '*' with list = everything):\n" +
        "  list: -   - every terminal with its state (starting, ready, app down, crashed, stopped ...), address, uptime and unread error lines\n" +
        "  read: name*, optionsJson {since (line number), lines (1-500, default 150), grep (plain text)}   - without since: the newest lines after where you read last, and a digest of errors; the cursor moves to the end. " +
        "With since: the next lines after it, in order (older lines come from the stored log), and where the next page starts. With grep: matching lines in order; a search does not move the cursor\n" +
        "  send: name*, optionsJson {text, enter (default true)}   - types a line into the program, as an answer to its prompt. Automatic for typed actions; for a script the owner approves it\n" +
        "  rerun: name*   - starts the terminal's last command again (it must not be running). The owner's approval is reused while the command and the project files are unchanged; a script always asks\n" +
        "  restart: name*   - stops it (Ctrl+C, then terminate, then kill) and starts it again: after a change hot reload cannot apply (packages, startup configuration)\n" +
        "  stop: name*   - stops it the same way\n" +
        "  clear: name*   - clears what the Console shows and moves your read cursor to the end\n" +
        "  close: name*   - stops it and removes the terminal; its runs stay in the history\n" +
        "RULES: prefer watch modes (watch, npm run dev, uvicorn --reload) so edits need no restart. A read without since gives only what is new; page through older output with since. " +
        "If the owner stopped a terminal, do not start it again unless the user asks. Do not build a project that a watch terminal already rebuilds; read the terminal instead.\n" +
        "RETURNS: the terminal's state and the lines asked for; for rerun and restart the same report as the call that started it.")]
    public Task<string> Terminal(
        [Description("One of: list, read, send, rerun, restart, stop, clear, close")] string action,
        [Description("The terminal's name, as list shows it. Not needed for list.")] string? name = null,
        [Description("Workspace name as shown in Waymark; leave empty for the computer's system terminals; '*' with list for every terminal.")] string? workspace = null,
        [Description("JSON object with the options of the action: read {\"since\":120,\"lines\":80,\"grep\":\"error\"}, send {\"text\":\"y\",\"enter\":true}. Omit when none are needed.")]
        string? optionsJson = null,
        [Description("For send, rerun and restart: one sentence on why. The owner reads it before approving and it is kept in the audit log.")] string? purpose = null,
        [Description("For rerun, restart and an approved send: seconds to wait for the result (default 30, at most about 45).")] int? waitSeconds = null,
        CancellationToken cancellationToken = default) =>
        service.TerminalAsync(workspace, action, name, optionsJson, purpose, waitSeconds, cancellationToken);
}
