using System.Globalization;
using System.Text.Json;
using ModelContextProtocol;
using Waymark.Core.Commands;
using Waymark.Core.Security;
using Waymark.Core.Tools;
using Waymark.Core.Workspaces;
using Waymark.Modules.Commands.Execution;
using Waymark.Modules.Commands.Policy;
using Waymark.Modules.Commands.Recipes;
using Waymark.Modules.Commands.Reporting;
using Waymark.Modules.Commands.Terminals;

namespace Waymark.Modules.Commands;

/// <summary>
/// What the MCP tools call. Checks the request, builds the command from a recipe (or takes the shell script), hands it to the coordinator and
/// turns the result into the short report Claude reads. Every failure that Claude can correct is thrown as a message it can act on.
/// A command that keeps running (an app, a dev server, a watcher, a script given a terminal) goes to a named terminal; the report comes back as soon
/// as it is up, it crashed, or it waits for the owner, never when it ends.
/// </summary>
public sealed class CommandService(
    IPathGuard guard,
    CommandCoordinator coordinator,
    TerminalCoordinator terminals,
    CommandPolicy policy,
    ShellLocator shells,
    ProcRecipes proc,
    PythonRecipes python,
    NodeRecipes node,
    IWorkspaceStore workspaces,
    ToolPipelineOptions pipeline,
    CommandsOptions options,
    TimeProvider time)
{
    public const string ClaudeActor = TerminalActors.Claude;
    private const int DefaultWaitSeconds = 30;
    private const int MinCallMarginSeconds = 12;
    private const int MaxScriptChars = 20_000;
    private const int MaxPurposeChars = 300;
    private const int DefaultShellTimeoutSeconds = 300;
    private const int MaxShellTimeoutSeconds = 3600;
    private const int MaxOutputLines = 500;
    private const int MaxListed = 30;
    private const int DefaultReadLines = 150;
    private const int MaxInputChars = 4_000;
    private static readonly TimeSpan PlainStopWait = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan MaxStopWait = TimeSpan.FromSeconds(40);
    private static readonly TimeSpan AfterInput = TimeSpan.FromMilliseconds(1500);

    /// <summary>The project toolchains: a .NET action (build, test, ef_...), a Python action (py_test, py_install ...) or a Node action (node_test, node_install ...). The action's name says which; they never overlap.</summary>
    public Task<string> ProjectAsync(string workspace, string action, string purpose, string? optionsJson, int? waitSeconds, CancellationToken ct)
    {
        var name = action?.Trim().ToLowerInvariant() ?? string.Empty;
        if (PythonRecipes.Handles(name))
        {
            return RecipeAsync(workspace, purpose, waitSeconds, optionsJson, (context, token) => python.PlanAsync(name, context, token), ct);
        }

        return NodeRecipes.Handles(name)
            ? RecipeAsync(workspace, purpose, waitSeconds, optionsJson, (context, token) => node.PlanAsync(name, context, token), ct)
            : RecipeAsync(workspace, purpose, waitSeconds, optionsJson, (context, _) => Task.FromResult(DotnetRecipes.Plan(name, context)), ct);
    }

    public Task<string> GitAsync(string workspace, string action, string purpose, string? optionsJson, int? waitSeconds, CancellationToken ct) =>
        RecipeAsync(workspace, purpose, waitSeconds, optionsJson, (context, _) => Task.FromResult(GitRecipes.Plan(action, context)), ct);

    public async Task<string> ProcAsync(string workspace, string action, string purpose, string? optionsJson, int? waitSeconds, CancellationToken ct)
    {
        // A port held by a terminal's program is stopped through the terminal, so the terminal knows it ended and who ended it.
        if (string.Equals(action?.Trim(), "stop_port", StringComparison.OrdinalIgnoreCase) && PortOf(optionsJson) is { } port)
        {
            var live = await terminals.Store.ListAsync(new TerminalQuery(LiveOnly: true), ct).ConfigureAwait(false);
            if (live.FirstOrDefault(t => t.Ports.Contains(port) && terminals.IsRunning(t)) is { } owner)
            {
                var scope = owner.WorkspaceId is null ? string.Empty : $", workspace='{owner.WorkspaceName}'";
                throw new McpException(
                    $"Port {port} belongs to terminal '{owner.Name}' ({owner.Scope}, run #{owner.CurrentRunId}). " +
                    $"Stop it with cmd_terminal(action='stop', name='{owner.Name}'{scope}) so the terminal knows it ended.");
            }
        }

        return await RecipeAsync(workspace, purpose, waitSeconds, optionsJson, (context, token) => proc.PlanAsync(action ?? string.Empty, context, token), ct).ConfigureAwait(false);
    }

    public async Task<string> ShellAsync(
        string? workspaceName,
        string script,
        string purpose,
        string? shellId,
        string? cwd,
        int? timeoutSeconds,
        int? waitSeconds,
        CancellationToken ct,
        string? terminal = null,
        string? readyPattern = null,
        int? readyPort = null)
    {
        try
        {
            var cleanPurpose = CleanPurpose(purpose);
            var text = (script ?? string.Empty).Trim();
            if (text.Length == 0 || text.Length > MaxScriptChars || text.Contains('\0', StringComparison.Ordinal))
            {
                throw new ArgumentException($"The script must have 1 to {MaxScriptChars} characters and no NUL characters.");
            }

            var shell = shells.Resolve(shellId)
                ?? throw new ArgumentException(shells.Available.Count == 0
                    ? "No shell was found on this computer."
                    : $"Shell '{shellId}' is not installed. Available: {string.Join(", ", shells.Available.Select(s => s.Id))}. Leave 'shell' empty to pick automatically.");

            Workspace? workspace = string.IsNullOrWhiteSpace(workspaceName) ? null : await guard.GetWorkspaceAsync(workspaceName, ct).ConfigureAwait(false);
            var folder = ResolveFolder(workspace, cwd);
            var keepsRunning = !string.IsNullOrWhiteSpace(terminal);
            int? maxMinutes = null;
            var seconds = timeoutSeconds ?? DefaultShellTimeoutSeconds;
            if (keepsRunning)
            {
                // In a terminal the script runs until it is stopped; timeoutSeconds, when given, is the most it may run.
                maxMinutes = timeoutSeconds is { } limit ? Math.Max(1, (int)Math.Ceiling(limit / 60.0)) : null;
            }
            else if (seconds < 5 || seconds > MaxShellTimeoutSeconds)
            {
                throw new ArgumentException($"timeoutSeconds must be between 5 and {MaxShellTimeoutSeconds}.");
            }

            if (!keepsRunning && (readyPattern is not null || readyPort is not null))
            {
                throw new ArgumentException("readyPattern and readyPort apply to a script that keeps running: give it a 'terminal' name too.");
            }

            var submission = new CommandSubmission(
                workspace,
                workspace?.Name ?? "system",
                CommandCatalog.Get(CommandCatalog.ShellRun),
                CommandTier.Shell,
                text,
                folder,
                cleanPurpose,
                ClaudeActor,
                TimeSpan.FromSeconds(seconds),
                null,
                shell,
                text,
                keepsRunning ? RiskHints.ScanForTerminal(text) : RiskHints.Scan(text));
            if (!keepsRunning)
            {
                return await SubmitAndReportAsync(submission, workspace?.Path, waitSeconds, ct).ConfigureAwait(false);
            }

            var name = TerminalNameOf(terminal!);
            var pattern = string.IsNullOrWhiteSpace(readyPattern) ? null : readyPattern.Trim();
            if (pattern is { Length: > 200 })
            {
                throw new ArgumentException("readyPattern is longer than 200 characters.");
            }

            if (readyPort is { } port && (port < 1 || port > 65535))
            {
                throw new ArgumentException("readyPort must be between 1 and 65535.");
            }

            var settings = new TerminalSettings(name, pattern, readyPort, TerminalSettings.DefaultReadyTimeoutSeconds, false, maxMinutes);
            return await StartTerminalAsync(submission, settings, name, [], [], [], null, isShell: true, workspace?.Path, waitSeconds, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsRequestError(ex))
        {
            throw new McpException(ex.Message);
        }
    }

    public async Task<string> WaitAsync(long runId, int? waitSeconds, CancellationToken ct)
    {
        var run = await coordinator.WaitAsync(runId, ClampWait(waitSeconds), ct).ConfigureAwait(false)
            ?? throw new McpException($"Run #{runId} does not exist.");
        return await ReportAsync(run, ct).ConfigureAwait(false);
    }

    public async Task<string> StopAsync(long runId, CancellationToken ct)
    {
        var existing = await coordinator.GetAsync(runId, ct).ConfigureAwait(false) ?? throw new McpException($"Run #{runId} does not exist.");
        if (!existing.Status.IsActive() || !await coordinator.StopAsync(runId, ClaudeActor, ct).ConfigureAwait(false))
        {
            return $"Run #{runId} is not active (status: {existing.Status}).";
        }

        // A terminal program stops the way a developer stops it (Ctrl+C, then terminate), so it gets the time that takes, within the call's limit.
        var wait = existing.TerminalId is null ? PlainStopWait : terminals.StopWait < MaxStopWait ? terminals.StopWait : MaxStopWait;
        await coordinator.WaitEndedAsync([runId], wait).ConfigureAwait(false);
        var stopped = await coordinator.GetAsync(runId, ct).ConfigureAwait(false) ?? existing;
        return await ReportAsync(stopped, ct).ConfigureAwait(false);
    }

    public async Task<string> OutputAsync(long runId, int? fromLine, int? lines, string? grep, CancellationToken ct)
    {
        var run = await coordinator.GetAsync(runId, ct).ConfigureAwait(false) ?? throw new McpException($"Run #{runId} does not exist.");
        var count = Math.Clamp(lines ?? (string.IsNullOrWhiteSpace(grep) ? 150 : 100), 1, MaxOutputLines);

        // A terminal's output is its streamed log, which can be far longer than what memory keeps: read with the same line numbers as cmd_terminal.
        if (run.TerminalId is not null && TerminalOutput(run, fromLine, count, grep) is { } terminalOutput)
        {
            return terminalOutput;
        }

        var plain = await coordinator.ReadPlainOutputAsync(runId, ct).ConfigureAwait(false) ?? string.Empty;
        var all = plain.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        while (all.Count > 0 && all[^1].Length == 0)
        {
            all.RemoveAt(all.Count - 1);
        }

        var output = new OutputBudget(pipeline.MaxOutputChars);
        var invariant = CultureInfo.InvariantCulture;
        if (!string.IsNullOrWhiteSpace(grep))
        {
            var needle = grep.Trim();
            var hits = all.Select((text, index) => (text, number: index + 1)).Where(x => x.text.Contains(needle, StringComparison.OrdinalIgnoreCase)).ToList();
            output.TryAppendLine(string.Create(invariant, $"Run #{runId} ({run.Status}) - {hits.Count} of {all.Count} lines contain '{needle}' (showing {Math.Min(count, hits.Count)}):"));
            foreach (var (text, number) in hits.Take(count))
            {
                output.TryAppendLine(string.Create(invariant, $"{number,6} | {text}"));
            }

            return output.Build().TrimEnd();
        }

        var first = fromLine is { } requested && requested >= 1 ? requested : Math.Max(1, all.Count - count + 1);
        var window = all.Skip(first - 1).Take(count).ToList();
        output.TryAppendLine(window.Count == 0
            ? string.Create(invariant, $"Run #{runId} ({run.Status}) - there is no line {first}; the output has {all.Count} lines.")
            : string.Create(invariant, $"Run #{runId} ({run.Status}) - lines {first}-{first + window.Count - 1} of {all.Count}:"));
        for (var i = 0; i < window.Count; i++)
        {
            output.TryAppendLine(string.Create(invariant, $"{first + i,6} | {window[i]}"));
        }

        if (run.Status.IsActive())
        {
            output.TryAppendLine(run.TerminalId is null
                ? "(the command is still running; call again for newer lines)"
                : "(it keeps running in a terminal; cmd_terminal(action='read') gives only the lines that are new since your last read)");
        }

        return output.Build().TrimEnd();
    }

    // The output of a terminal run, from its log: while it runs from the live log, after it ended from the stored files. Null when there is no log
    // (it never started), so the caller shows what the run record has.
    private string? TerminalOutput(CommandRun run, int? fromLine, int count, string? grep)
    {
        var runtime = coordinator.GetTerminalRuntime(run.Id);
        var since = Math.Max(0, (fromLine ?? 1) - 1);
        var needle = string.IsNullOrWhiteSpace(grep) ? null : grep.Trim();
        LogRead read;
        if (runtime is not null)
        {
            read = needle is not null ? runtime.Log.GrepPage(needle, since, count)
                : fromLine is not null ? runtime.Log.ReadPage(since, count)
                : runtime.Log.Read(0, count);
        }
        else if (run.OutputPath is { } path && File.Exists(path))
        {
            read = needle is not null ? TerminalLog.ReadStored(path, options.TerminalLogFiles, since, count, needle)
                : fromLine is not null ? TerminalLog.ReadStored(path, options.TerminalLogFiles, since, count)
                : TerminalLog.TailStored(path, options.TerminalLogFiles, count);
        }
        else
        {
            return null;
        }

        var invariant = CultureInfo.InvariantCulture;
        var output = new OutputBudget(pipeline.MaxOutputChars);
        if (needle is not null)
        {
            var more = read.After > 0 ? string.Create(invariant, $", {read.After} more after the last one shown") : string.Empty;
            output.TryAppendLine(string.Create(invariant, $"Run #{run.Id} ({run.Status}) - {read.Lines.Count} line(s) of {read.Total} after line {since} contain '{needle}'{more}:"));
        }
        else
        {
            output.TryAppendLine(read.Lines.Count == 0
                ? string.Create(invariant, $"Run #{run.Id} ({run.Status}) - there is no line {since + 1}; the output has {read.Total} lines.")
                : string.Create(invariant, $"Run #{run.Id} ({run.Status}) - lines {read.Lines[0].Number}-{read.Lines[^1].Number} of {read.Total}:"));
        }

        foreach (var line in read.Lines)
        {
            output.TryAppendLine(string.Create(invariant, $"{line.Number,6} | {line.Text}"));
        }

        if (read.Lines.Count > 0 && read.After > 0)
        {
            output.TryAppendLine(string.Create(invariant, $"(more after: fromLine={read.Lines[^1].Number + 1})"));
        }

        if (read.OldestKept > 1)
        {
            output.TryAppendLine(string.Create(invariant, $"(lines before {read.OldestKept} are no longer stored: the log keeps only its newest files)"));
        }

        if (run.Status.IsActive())
        {
            output.TryAppendLine("(it keeps running in a terminal; cmd_terminal(action='read') gives only the lines that are new since your last read)");
        }

        return output.Build().TrimEnd();
    }

    public async Task<string> ListAsync(string? workspaceName, int? limit, CancellationToken ct)
    {
        try
        {
            long? workspaceId = null;
            if (!string.IsNullOrWhiteSpace(workspaceName))
            {
                workspaceId = (await guard.GetWorkspaceAsync(workspaceName, ct).ConfigureAwait(false)).Id;
            }

            var take = Math.Clamp(limit ?? 10, 1, MaxListed);
            var runs = await coordinator.ListAsync(new CommandRunQuery(WorkspaceId: workspaceId, Limit: take), ct).ConfigureAwait(false);
            if (runs.Count == 0)
            {
                return "No commands have been requested yet.";
            }

            var now = time.GetUtcNow();
            var output = new OutputBudget(pipeline.MaxOutputChars);
            output.TryAppendLine(string.Create(CultureInfo.InvariantCulture, $"{runs.Count} most recent command(s), newest first:"));
            foreach (var run in runs)
            {
                output.TryAppendLine(Row(run, now));
            }

            if (runs.Any(r => r.TerminalId is not null))
            {
                output.TryAppendLine("Runs marked [terminal] keep running in a terminal: cmd_terminal(action='list') shows the terminals.");
            }

            return output.Build().TrimEnd();
        }
        catch (Exception ex) when (IsRequestError(ex))
        {
            throw new McpException(ex.Message);
        }
    }

    /// <summary>
    /// One terminal action: list, read (new output since a line), send (a line typed to the program), rerun (its last command again), restart (stop,
    /// then rerun), stop, clear (what the Console shows and the read cursor) or close (stop and remove the terminal).
    /// </summary>
    public async Task<string> TerminalAsync(string? workspaceName, string action, string? name, string? optionsJson, string? purpose, int? waitSeconds, CancellationToken ct)
    {
        try
        {
            var verb = action?.Trim().ToLowerInvariant() ?? string.Empty;
            var all = string.Equals(workspaceName?.Trim(), "*", StringComparison.Ordinal);
            Workspace? workspace = all || string.IsNullOrWhiteSpace(workspaceName) ? null : await guard.GetWorkspaceAsync(workspaceName, ct).ConfigureAwait(false);
            var options = RecipeOptions.Parse(optionsJson);
            var now = time.GetUtcNow();
            if (verb == "list")
            {
                options.EnsureNoUnknown();
                var query = all ? new TerminalQuery() : workspace is null ? new TerminalQuery(SystemOnly: true) : new TerminalQuery(WorkspaceId: workspace.Id);
                var list = await terminals.ListAsync(query, ct).ConfigureAwait(false);
                var scope = all ? "all workspaces and the computer" : workspace is null ? "the computer (system terminals)" : $"workspace '{workspace.Name}'";
                return TerminalReport.List(list, scope, now, pipeline.MaxOutputChars);
            }

            if (all)
            {
                throw new ArgumentException("workspace '*' works with action 'list' only; name one workspace, or leave it out for a system terminal.");
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("'name' is required: the terminal's name, as cmd_terminal(action='list') shows it.");
            }

            var terminalName = name.Trim().ToLowerInvariant();
            var terminal = await terminals.Store.GetByNameAsync(workspace?.Id, terminalName, ct).ConfigureAwait(false)
                ?? throw new ArgumentException(await UnknownTerminalAsync(workspace, terminalName, ct).ConfigureAwait(false));
            switch (verb)
            {
                case "read":
                {
                    var since = options.Number("since", 0, int.MaxValue);
                    var lines = options.Number("lines", 1, MaxOutputLines) ?? DefaultReadLines;
                    var grep = options.Text("grep", 200);
                    options.EnsureNoUnknown();
                    return await ReadTerminalAsync(terminal, since, lines, grep, now, ct).ConfigureAwait(false);
                }

                case "send":
                {
                    var text = options.Text("text", MaxInputChars) ?? string.Empty;
                    var enter = options.Flag("enter", true);
                    options.EnsureNoUnknown();
                    if (text.Length == 0 && !enter)
                    {
                        throw new ArgumentException("Give 'text' to type, or leave 'enter' true to press Enter alone.");
                    }

                    return await SendAsync(terminal, workspace, text, enter, purpose, waitSeconds, now, ct).ConfigureAwait(false);
                }

                case "rerun":
                case "restart":
                    options.EnsureNoUnknown();
                    return await RerunAsync(terminal, workspace, verb == "restart", purpose, waitSeconds, ct).ConfigureAwait(false);

                case "stop":
                {
                    options.EnsureNoUnknown();
                    if (!terminals.IsRunning(terminal) || terminal.CurrentRunId is not { } runId)
                    {
                        return $"Nothing runs in terminal '{terminal.Name}' now ({terminal.State.Word()}).";
                    }

                    return await StopAsync(runId, ct).ConfigureAwait(false);
                }

                case "clear":
                {
                    options.EnsureNoUnknown();
                    if (terminal.CurrentRunId is not { } runId || !coordinator.ClearTerminalView(runId))
                    {
                        return $"Nothing runs in terminal '{terminal.Name}' now; there is nothing to clear.";
                    }

                    return $"Terminal '{terminal.Name}' is cleared: the Console shows only what comes next, and your next read starts here.";
                }

                case "close":
                {
                    options.EnsureNoUnknown();
                    var wasRunning = terminals.IsRunning(terminal);
                    await terminals.CloseAsync(terminal, ClaudeActor, ct).ConfigureAwait(false);
                    return wasRunning
                        ? $"Terminal '{terminal.Name}' was stopped and closed. Its runs stay in the history."
                        : $"Terminal '{terminal.Name}' was closed. Its runs stay in the history.";
                }

                default:
                    throw new ArgumentException($"Unknown terminal action '{action}'. Actions: list, read, send, rerun, restart, stop, clear, close.");
            }
        }
        catch (Exception ex) when (IsRequestError(ex))
        {
            throw new McpException(ex.Message);
        }
    }

    private async Task<string> ReadTerminalAsync(Terminal terminal, int? since, int lines, string? grep, DateTimeOffset now, CancellationToken ct)
    {
        var runtime = terminals.RuntimeOf(terminal);
        if (runtime is not null)
        {
            var from = since ?? runtime.ReadLine;
            LogRead read;
            LogRead? hits = null;
            if (grep is not null)
            {
                // A search shows matches only and leaves the cursor where it was, so the next plain read still shows everything new.
                hits = runtime.Log.GrepPage(grep, from, lines);
                read = new LogRead([], 0, runtime.Log.LineCount, hits.OldestKept);
            }
            else if (since is not null)
            {
                // A page: the next lines in order, from the files when they are older than memory.
                read = runtime.Log.ReadPage(from, lines);
                runtime.MarkRead(read.Lines.Count > 0 ? read.Lines[^1].Number : Math.Min(from, read.Total));
            }
            else
            {
                read = runtime.Log.Read(from, lines);
                runtime.MarkRead(read.Total);
            }

            return TerminalReport.Read(terminal, runtime, read, from, hits, grep, now, pipeline.MaxOutputChars, runtime.ReadLine);
        }

        // Nothing runs: the output of the last program, from its stored log, with the numbers it had while it ran.
        var last = terminal.LastRunId is { } lastId ? await coordinator.GetAsync(lastId, ct).ConfigureAwait(false) : null;
        if (last?.OutputPath is not { } path || !File.Exists(path))
        {
            var empty = new LogRead([], 0, 0, 1);
            return TerminalReport.Read(terminal, null, empty, since ?? 0, null, null, now, pipeline.MaxOutputChars, 0);
        }

        var start = since ?? 0;
        if (grep is not null)
        {
            var found = TerminalLog.ReadStored(path, options.TerminalLogFiles, start, lines, grep);
            return TerminalReport.Read(terminal, null, new LogRead([], 0, found.Total, found.OldestKept), start, found, grep, now, pipeline.MaxOutputChars, found.Total);
        }

        var stored = since is null ? TerminalLog.TailStored(path, options.TerminalLogFiles, lines) : TerminalLog.ReadStored(path, options.TerminalLogFiles, start, lines);
        return TerminalReport.Read(terminal, null, stored, start, null, null, now, pipeline.MaxOutputChars, stored.Total);
    }

    private async Task<string> SendAsync(Terminal terminal, Workspace? workspace, string text, bool enter, string? purpose, int? waitSeconds, DateTimeOffset now, CancellationToken ct)
    {
        if (!terminals.IsRunning(terminal) || terminal.CurrentRunId is not { } runId)
        {
            throw new ArgumentException($"Nothing runs in terminal '{terminal.Name}' now ({terminal.State.Word()}). Start it again with action 'rerun'.");
        }

        var isShell = terminal.Launch?.Tier == CommandTier.Shell;
        var operation = CommandCatalog.Get(isShell ? CommandCatalog.TerminalShellInput : CommandCatalog.TerminalInput);
        var gate = (await policy.EvaluateAsync(operation, workspace, readFingerprint: false, ct).ConfigureAwait(false)).Gate;
        if (gate == CommandGate.Off)
        {
            throw new ArgumentException($"'{operation.Label}' is switched off. The owner can change this on the Commands page of the admin UI.");
        }

        var runtime = terminals.RuntimeOf(terminal);
        var before = runtime?.Log.LineCount ?? 0;
        if (gate == CommandGate.Auto)
        {
            var problem = await coordinator.SendInputAsync(runId, text, enter, ClaudeActor, ct).ConfigureAwait(false);
            if (problem is not null)
            {
                throw new ArgumentException($"Terminal '{terminal.Name}': {problem}");
            }

            await Task.Delay(AfterInput, ct).ConfigureAwait(false);
            var updated = await terminals.Store.GetAsync(terminal.Id, ct).ConfigureAwait(false) ?? terminal;
            runtime = terminals.RuntimeOf(updated);
            if (runtime is null)
            {
                return $"Sent to terminal '{terminal.Name}'. The program has ended since: cmd_terminal(action='read', name='{terminal.Name}').";
            }

            var read = runtime.Log.Read(before, 40);
            runtime.MarkRead(read.Total);
            return "Sent. What it printed since:\n" + TerminalReport.Read(updated, runtime, read, before, null, null, now, pipeline.MaxOutputChars, runtime.ReadLine);
        }

        // The owner approves what is typed into a script (it can be any command): the request waits on the approval card like a command.
        var shown = SensitiveDataScanner.Redact(text).Text.ReplaceLineEndings(" ");
        var line = $"type into terminal {terminal.Name}: {shown}{(enter ? " ⏎" : string.Empty)}";
        InProcessAction write = async (output, stop) =>
        {
            var problem = await coordinator.SendInputAsync(runId, text, enter, ClaudeActor, stop).ConfigureAwait(false);
            output(problem is null ? "Sent.\n" : problem + "\n");
            return problem is null ? 0 : 1;
        };
        var folder = Directory.Exists(terminal.Folder) ? terminal.Folder : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var plan = new CommandPlan(operation.Key, "input", [], TimeSpan.FromMinutes(1), write, line, null, folder);
        var submission = new CommandSubmission(
            workspace,
            workspace?.Name ?? "system",
            operation,
            CommandTier.Recipe,
            line,
            folder,
            CleanPurpose(purpose ?? $"Answer the program running in terminal '{terminal.Name}'."),
            ClaudeActor,
            TimeSpan.FromMinutes(1),
            plan,
            null,
            null,
            []);
        return await SubmitAndReportAsync(submission, workspace?.Path, waitSeconds, ct).ConfigureAwait(false);
    }

    private async Task<string> RerunAsync(Terminal terminal, Workspace? workspace, bool restart, string? purpose, int? waitSeconds, CancellationToken ct)
    {
        if (terminal.Launch is not { } spec)
        {
            throw new ArgumentException($"Terminal '{terminal.Name}' has no command to start again. Start one with a run action (run, watch, node_run, py_run) or cmd_shell with terminal='{terminal.Name}'.");
        }

        if (!restart && terminals.IsRunning(terminal))
        {
            throw new ArgumentException($"Terminal '{terminal.Name}' runs already. Use action 'restart' to stop it and start it again.");
        }

        if (terminal.WorkspaceId is not null && workspace is null)
        {
            workspace = await guard.GetWorkspaceAsync(terminal.WorkspaceName, ct).ConfigureAwait(false);
        }

        var cleanPurpose = CleanPurpose(purpose ?? $"{(restart ? "Restart" : "Start again")} terminal '{terminal.Name}': {Short(spec.CommandLine)}");
        var settings = new TerminalSettings(terminal.Name, null, spec.ReadyPort, spec.ReadyTimeoutSeconds, restart, spec.MaxMinutes);
        if (!Directory.Exists(spec.WorkingDirectory))
        {
            throw new ArgumentException($"The folder of terminal '{terminal.Name}' does not exist any more: {spec.WorkingDirectory}");
        }

        if (spec.Tier == CommandTier.Shell)
        {
            var shell = shells.Resolve(spec.ShellId) ?? throw new ArgumentException($"The shell '{spec.ShellId}' of terminal '{terminal.Name}' was not found on this computer.");
            var script = spec.Script ?? throw new ArgumentException($"Terminal '{terminal.Name}' has no script to start again.");
            var shellSubmission = new CommandSubmission(
                workspace,
                workspace?.Name ?? "system",
                CommandCatalog.Get(CommandCatalog.ShellRun),
                CommandTier.Shell,
                script,
                spec.WorkingDirectory,
                cleanPurpose,
                ClaudeActor,
                Timeout.InfiniteTimeSpan,
                null,
                shell,
                script,
                RiskHints.ScanForTerminal(script));
            return await StartTerminalAsync(shellSubmission, settings, terminal.Name, spec.ReadyPatterns, spec.AppDownPatterns, spec.AppUpPatterns, null, isShell: true, workspace?.Path, waitSeconds, ct).ConfigureAwait(false);
        }

        if (workspace is null)
        {
            throw new ArgumentException($"Terminal '{terminal.Name}' belongs to a workspace; name it with 'workspace'.");
        }

        var prepare = spec.PrepareProgram is { } builder ? new CommandPlan("dotnet.build", builder, spec.PrepareArguments, TimeSpan.FromMinutes(15)) : null;
        var plan = new CommandPlan(spec.Operation, spec.Program ?? "program", spec.Arguments, Timeout.InfiniteTimeSpan, null, spec.CommandLine, spec.ExecutablePath, spec.WorkingDirectory)
        {
            Lifetime = PlanLifetime.Terminal,
            Terminal = settings,
            Prepare = prepare,
            ReadyPatterns = spec.ReadyPatterns,
            AppDownPatterns = spec.AppDownPatterns,
            AppUpPatterns = spec.AppUpPatterns,
            DefaultTerminalName = terminal.Name,
        };
        var submission = new CommandSubmission(
            workspace,
            workspace.Name,
            CommandCatalog.Get(spec.Operation),
            CommandTier.Recipe,
            plan.CommandLine,
            spec.WorkingDirectory,
            cleanPurpose,
            ClaudeActor,
            Timeout.InfiniteTimeSpan,
            plan,
            null,
            null,
            []);
        return await StartTerminalAsync(submission, settings, terminal.Name, spec.ReadyPatterns, spec.AppDownPatterns, spec.AppUpPatterns, prepare, isShell: false, workspace.Path, waitSeconds, ct).ConfigureAwait(false);
    }

    private async Task<string> RecipeAsync(
        string workspaceName,
        string purpose,
        int? waitSeconds,
        string? optionsJson,
        Func<RecipeContext, CancellationToken, Task<CommandPlan>> plan,
        CancellationToken ct)
    {
        try
        {
            var cleanPurpose = CleanPurpose(purpose);
            var workspace = await guard.GetWorkspaceAsync(workspaceName, ct).ConfigureAwait(false);
            var context = new RecipeContext(workspace, guard, RecipeOptions.Parse(optionsJson));
            var built = await plan(context, ct).ConfigureAwait(false);
            var root = Path.GetFullPath(workspace.Path);
            var submission = new CommandSubmission(
                workspace,
                workspace.Name,
                CommandCatalog.Get(built.Operation),
                CommandTier.Recipe,
                built.CommandLine,
                built.WorkingDirectory ?? root,
                cleanPurpose,
                ClaudeActor,
                built.Timeout,
                built,
                null,
                null,
                []);
            if (built.Lifetime == PlanLifetime.Terminal)
            {
                var settings = built.Terminal ?? new TerminalSettings(null, null, null, TerminalSettings.DefaultReadyTimeoutSeconds, false, null);
                var name = settings.Name ?? built.DefaultTerminalName ?? TerminalNames.ForFolder(submission.WorkingDirectory, "app");
                return await StartTerminalAsync(submission, settings, name, built.ReadyPatterns, built.AppDownPatterns, built.AppUpPatterns, built.Prepare, isShell: false, root, waitSeconds, ct).ConfigureAwait(false);
            }

            return await SubmitAndReportAsync(submission, root, waitSeconds, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsRequestError(ex))
        {
            throw new McpException(ex.Message);
        }
    }

    // Starts a command in its terminal: finds or creates the terminal, refuses a second program there (or replaces it when asked), keeps what is
    // needed to start it again, and submits it with the terminal attached. The report comes when it is up, crashed or waits for the owner.
    private async Task<string> StartTerminalAsync(
        CommandSubmission baseSubmission,
        TerminalSettings settings,
        string defaultName,
        IReadOnlyList<string> recipeReady,
        IReadOnlyList<string> appDown,
        IReadOnlyList<string> appUp,
        CommandPlan? prepare,
        bool isShell,
        string? workspaceRoot,
        int? waitSeconds,
        CancellationToken ct)
    {
        var name = TerminalNameOf(settings.Name ?? defaultName);
        SubmitResult submitted;

        // From the checks to the registered run, one start at a time: two starts cannot both find the terminal free or room for one more.
        using (await terminals.EnterStartAsync(ct).ConfigureAwait(false))
        {
            var preparation = await terminals.PrepareAsync(
                baseSubmission.Workspace,
                baseSubmission.WorkspaceName,
                name,
                baseSubmission.WorkingDirectory,
                baseSubmission.Operation.Key,
                baseSubmission.CommandLine,
                settings.Replace,
                ct).ConfigureAwait(false);
            if (preparation is { IsDuplicate: true, Running: { } running })
            {
                var current = await ReportAsync(running, ct, workspaceRoot).ConfigureAwait(false);
                return $"Terminal '{name}' already runs this same command (run #{running.Id}); nothing new was started.\n\n{current}";
            }

            try
            {
                submitted = await SubmitInTerminalAsync(baseSubmission, settings, preparation, name, recipeReady, appDown, appUp, prepare, isShell, ct).ConfigureAwait(false);
            }
            catch when (preparation.Created)
            {
                await RemoveNewTerminalAsync(preparation.Terminal).ConfigureAwait(false);
                throw;
            }

            if (submitted.IsRefused)
            {
                // Switched off: nothing started, so a terminal made for this start goes again.
                if (preparation.Created)
                {
                    await RemoveNewTerminalAsync(preparation.Terminal).ConfigureAwait(false);
                }

                throw new McpException(submitted.RefusalReason!);
            }
        }

        var run = await coordinator.WaitAsync(submitted.Run.Id, ClampWait(waitSeconds), ct).ConfigureAwait(false) ?? submitted.Run;
        return await ReportAsync(run, ct, workspaceRoot).ConfigureAwait(false);
    }

    private async Task<SubmitResult> SubmitInTerminalAsync(
        CommandSubmission baseSubmission,
        TerminalSettings settings,
        TerminalPreparation preparation,
        string name,
        IReadOnlyList<string> recipeReady,
        IReadOnlyList<string> appDown,
        IReadOnlyList<string> appUp,
        CommandPlan? prepare,
        bool isShell,
        CancellationToken ct)
    {
        // A text Claude names is what to wait for; the ecosystem's usual lines and any port count only when it names none.
        var explicitPattern = settings.ReadyPattern;
        IReadOnlyList<string> ready = explicitPattern is not null ? new List<string> { explicitPattern } : recipeReady.ToList();
        var plan = baseSubmission.Plan;
        var spec = new TerminalLaunchSpec(
            baseSubmission.Tier,
            baseSubmission.Operation.Key,
            baseSubmission.CommandLine,
            baseSubmission.WorkingDirectory,
            baseSubmission.Purpose,
            plan?.Program,
            plan?.ExecutablePath,
            plan?.Arguments ?? [],
            prepare?.Program,
            prepare?.Arguments ?? [],
            baseSubmission.Shell?.Id,
            baseSubmission.Script,
            ready,
            appDown,
            appUp,
            settings.ReadyPort,
            settings.ReadyTimeoutSeconds,
            settings.MaxMinutes);
        await terminals.Store.UpdateAsync(preparation.Terminal.Id, new TerminalUpdate { Launch = spec }, ct).ConfigureAwait(false);
        var launch = new TerminalLaunch(
            preparation.Terminal.Id,
            name,
            isShell,
            ready,
            appDown,
            appUp,
            settings.ReadyPort,
            TimeSpan.FromSeconds(settings.ReadyTimeoutSeconds),
            settings.MaxMinutes is { } minutes ? TimeSpan.FromMinutes(minutes) : null,
            prepare,
            preparation.Terminal.Approval,
            ReadyOnAnyPort: explicitPattern is null);
        var submission = baseSubmission with { Terminal = launch, Timeout = Timeout.InfiniteTimeSpan };
        return await coordinator.SubmitAsync(submission, ct).ConfigureAwait(false);
    }

    // A terminal this start created and nothing ran in: removed, so the list shows no idle terminal for a start that never happened.
    private async Task RemoveNewTerminalAsync(Terminal terminal)
    {
        try
        {
            await terminals.CloseAsync(terminal, ClaudeActor, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Left as an idle terminal; maintenance removes unused ones.
        }
    }

    private async Task<string> SubmitAndReportAsync(CommandSubmission submission, string? workspaceRoot, int? waitSeconds, CancellationToken ct)
    {
        var result = await coordinator.SubmitAsync(submission, ct).ConfigureAwait(false);
        if (result.IsRefused)
        {
            throw new McpException(result.RefusalReason!);
        }

        var run = await coordinator.WaitAsync(result.Run.Id, ClampWait(waitSeconds), ct).ConfigureAwait(false) ?? result.Run;
        return await ReportAsync(run, ct, workspaceRoot).ConfigureAwait(false);
    }

    private async Task<string> ReportAsync(CommandRun run, CancellationToken ct, string? workspaceRoot = null)
    {
        var root = workspaceRoot;
        if (root is null && run.WorkspaceId is { } id)
        {
            var all = await workspaces.ListAsync(ct).ConfigureAwait(false);
            root = all.FirstOrDefault(w => w.Id == id)?.Path;
        }

        var plain = await coordinator.ReadPlainOutputAsync(run.Id, ct).ConfigureAwait(false) ?? string.Empty;
        var fullRoot = root is null ? null : Path.GetFullPath(root);
        if (run.TerminalId is { } terminalId)
        {
            var terminal = await terminals.Store.GetAsync(terminalId, ct).ConfigureAwait(false);
            return TerminalReport.Build(run, terminal, coordinator.GetTerminalRuntime(run.Id), plain, fullRoot, time.GetUtcNow(), pipeline.MaxOutputChars);
        }

        return RunReport.Build(run, plain, fullRoot, time.GetUtcNow(), pipeline.MaxOutputChars);
    }

    private async Task<string> UnknownTerminalAsync(Workspace? workspace, string name, CancellationToken ct)
    {
        var query = workspace is null ? new TerminalQuery(SystemOnly: true) : new TerminalQuery(WorkspaceId: workspace.Id);
        var known = await terminals.Store.ListAsync(query, ct).ConfigureAwait(false);
        var where = workspace is null ? "among the system terminals (leave 'workspace' out only for those)" : $"in workspace '{workspace.Name}'";
        return known.Count == 0
            ? $"There is no terminal '{name}' {where}, and none at all yet. Start one with a run action or cmd_shell with 'terminal'."
            : $"There is no terminal '{name}' {where}. Terminals there: {string.Join(", ", known.Select(t => t.Name))}.";
    }

    private TimeSpan ClampWait(int? requested)
    {
        var most = Math.Max(0, (int)pipeline.CallTimeout.TotalSeconds - MinCallMarginSeconds);
        return TimeSpan.FromSeconds(Math.Clamp(requested ?? DefaultWaitSeconds, 0, most));
    }

    private string ResolveFolder(Workspace? workspace, string? cwd)
    {
        if (workspace is not null)
        {
            var guarded = guard.Resolve(workspace, cwd);
            return Directory.Exists(guarded.AbsolutePath)
                ? guarded.AbsolutePath
                : throw new ArgumentException($"The folder '{cwd}' does not exist in the workspace.");
        }

        if (string.IsNullOrWhiteSpace(cwd))
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        if (!Path.IsPathRooted(cwd) || !Directory.Exists(cwd))
        {
            throw new ArgumentException("Without a workspace, 'cwd' must be the full path of an existing folder.");
        }

        return Path.GetFullPath(cwd);
    }

    private static string TerminalNameOf(string name)
    {
        var trimmed = (name ?? string.Empty).Trim().ToLowerInvariant();
        return TerminalNames.Pattern().IsMatch(trimmed)
            ? trimmed
            : throw new ArgumentException($"'{name}' is not a terminal name: use lower-case letters, digits and '-', up to {TerminalNames.MaxLength} characters, starting with a letter or digit.");
    }

    private static int? PortOf(string? optionsJson)
    {
        if (string.IsNullOrWhiteSpace(optionsJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(optionsJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("port", out var port)
                && port.TryGetInt32(out var value) ? value : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string CleanPurpose(string? purpose)
    {
        var text = (purpose ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            throw new ArgumentException("'purpose' is required: say in one sentence why you run this. The owner reads it before approving.");
        }

        text = text.ReplaceLineEndings(" ");
        return text.Length <= MaxPurposeChars ? text : throw new ArgumentException($"'purpose' is longer than {MaxPurposeChars} characters.");
    }

    private static string Short(string commandLine)
    {
        var line = commandLine.ReplaceLineEndings(" ").Trim();
        var cut = line.IndexOf("   # runs:", StringComparison.Ordinal);
        if (cut > 0)
        {
            line = line[..cut];
        }

        return line.Length > 120 ? line[..120] + "..." : line;
    }

    private static bool IsRequestError(Exception ex) =>
        ex is ArgumentException or KeyNotFoundException or InvalidOperationException or FileAccessDeniedException;

    private static string Row(CommandRun run, DateTimeOffset now)
    {
        var icon = run.Status switch
        {
            CommandRunStatus.Succeeded => "✅",
            CommandRunStatus.Failed => "❌",
            CommandRunStatus.TimedOut => "⏱",
            CommandRunStatus.Cancelled => "🛑",
            CommandRunStatus.Denied => "⛔",
            CommandRunStatus.WaitingApproval => "⏳",
            CommandRunStatus.Queued => "⏳",
            _ => run.TerminalId is null ? "🔄" : "🟢",
        };
        var line = run.CommandLine.ReplaceLineEndings(" ");
        var shown = line.Length > 90 ? line[..90] + "..." : line;
        var age = now - run.RequestedUtc;
        var ago = age.TotalMinutes >= 1 ? string.Create(CultureInfo.InvariantCulture, $"{(int)age.TotalMinutes} min ago") : "just now";
        var tag = run.TerminalId is null ? string.Empty : " [terminal]";
        return string.Create(CultureInfo.InvariantCulture, $"#{run.Id} {icon} {run.Status} {run.Operation}{tag} [{run.WorkspaceName}] {shown} ({ago})");
    }
}
