using System.Globalization;
using Waymark.Core.Commands;
using Waymark.Core.Tools;
using Waymark.Modules.Commands.Execution;
using Waymark.Modules.Commands.Terminals;

namespace Waymark.Modules.Commands.Reporting;

/// <summary>
/// What Claude reads about terminals: the start of a command that keeps running (up, still starting, crashed, waiting for the owner), a read of its
/// new output with a short digest, and the list of terminals. Always ends with the next call that makes sense, never with "wait until it ends".
/// </summary>
public static class TerminalReport
{
    private const int TailWhileRunning = 25;
    private const int MaxLineChars = 300;

    /// <summary>The report after a command was started in a terminal, or after Claude asked about it.</summary>
    public static string Build(CommandRun run, Terminal? terminal, TerminalRuntime? runtime, string plainOutput, string? workspaceRoot, DateTimeOffset now, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(run);
        var name = terminal?.Name ?? "terminal";
        if (run.Status == CommandRunStatus.WaitingApproval || !run.Status.IsActive())
        {
            // Waiting or ended: the usual report (errors, failed tests, the end of the output), headed by the terminal.
            var lead = run.Status == CommandRunStatus.WaitingApproval
                ? $"Terminal '{name}': once approved, this keeps running until it is stopped."
                : $"Terminal '{name}': {EndText(run)}.";
            var body = RunReport.Build(run, CutLines(plainOutput), workspaceRoot, now, maxChars);
            var next = run.Status == CommandRunStatus.WaitingApproval
                ? string.Empty
                : $"\nStart it again: cmd_terminal(action='rerun', name='{name}'{ScopeArg(terminal)}). Read all of it: cmd_output(runId={run.Id}).";
            return lead + "\n" + body + next;
        }

        var output = new OutputBudget(maxChars);
        output.TryAppendLine(Header(run, terminal, runtime, now));
        output.TryAppendLine($"command: {FirstLine(run.CommandLine)}");
        output.TryAppendLine($"folder: {run.WorkingDirectory}");
        if (run.Purpose.Length > 0)
        {
            output.TryAppendLine($"purpose: {run.Purpose}");
        }

        if (terminal is not null && terminal.Urls.Count > 0)
        {
            output.TryAppendLine($"address: {string.Join(", ", terminal.Urls)}");
        }
        else if (runtime is not null && runtime.Detector.Urls.Count > 0)
        {
            output.TryAppendLine($"address: {string.Join(", ", runtime.Detector.Urls)}");
        }

        if (runtime is not null)
        {
            var errors = runtime.Health.ErrorsAfter(0);
            if (errors > 0)
            {
                output.TryAppendLine($"errors printed so far: {errors}");
                foreach (var line in runtime.Health.ErrorLinesAfter(0, 5))
                {
                    output.TryAppendLine(string.Create(CultureInfo.InvariantCulture, $"  {line.Number,6} | {Cut(line.Text)}"));
                }
            }

            if (runtime.PendingPrompt is { } prompt)
            {
                output.TryAppendLine($"it seems to wait for an answer: \"{prompt}\" → cmd_terminal(action='send', name='{name}'{ScopeArg(terminal)}, optionsJson='{{\"text\":\"...\"}}')");
            }
        }

        AppendTail(output, plainOutput, TailWhileRunning);
        output.TryAppendLine();
        output.TryAppendLine(NextSteps(name, terminal, runtime?.Log.LineCount ?? 0));
        return output.Build().TrimEnd();
    }

    /// <summary>
    /// A read of a terminal: what it printed after since, with a digest of errors and a prompt it waits at. A plain read shows the newest lines and
    /// moves the cursor to the end; a paged read (since given) shows the next lines in order and says where the next page starts; a search shows
    /// matching lines and leaves the cursor where it was.
    /// </summary>
    public static string Read(
        Terminal terminal,
        TerminalRuntime? runtime,
        LogRead read,
        long since,
        LogRead? grepHits,
        string? grep,
        DateTimeOffset now,
        int maxChars,
        long cursor)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        ArgumentNullException.ThrowIfNull(read);
        var invariant = CultureInfo.InvariantCulture;
        var output = new OutputBudget(maxChars);
        output.TryAppendLine(StateLine(terminal, runtime, now));
        if (runtime is not null)
        {
            var errors = runtime.Health.ErrorsAfter(since);
            var warnings = runtime.Health.WarningsAfter(since);
            output.TryAppendLine(string.Create(invariant, $"since line {since}: {errors} error line(s), {warnings} warning line(s)"));
            foreach (var line in runtime.Health.ErrorLinesAfter(since, 8))
            {
                output.TryAppendLine(string.Create(invariant, $"  error {line.Number,6} | {Cut(line.Text)}"));
            }

            if (runtime.PendingPrompt is { } prompt)
            {
                output.TryAppendLine($"it seems to wait for an answer: \"{prompt}\" → cmd_terminal(action='send', name='{terminal.Name}'{ScopeArg(terminal)}, optionsJson='{{\"text\":\"...\"}}')");
            }
        }

        output.TryAppendLine();
        var oldest = (grepHits ?? read).OldestKept;
        long? nextPage = null;
        if (grepHits is not null)
        {
            var more = grepHits.After > 0 && grepHits.Lines.Count > 0 ? string.Create(invariant, $" (+{grepHits.After} more after line {grepHits.Lines[^1].Number})") : string.Empty;
            output.TryAppendLine(string.Create(invariant, $"{grepHits.Lines.Count} line(s) after line {since} contain '{grep}'{more}:"));
            foreach (var line in grepHits.Lines)
            {
                output.TryAppendLine(string.Create(invariant, $"{line.Number,6} | {Cut(line.Text)}"));
            }

            nextPage = grepHits.After > 0 && grepHits.Lines.Count > 0 ? grepHits.Lines[^1].Number : null;
        }
        else if (read.Lines.Count == 0)
        {
            output.TryAppendLine(string.Create(invariant, $"No output after line {since} (the program has printed {read.Total} lines)."));
        }
        else
        {
            var first = read.Lines[0].Number;
            var last = read.Lines[^1].Number;
            var skipped = read.Skipped > 0
                ? string.Create(invariant, $" ({read.Skipped} earlier new line(s) left out: read them in order with since={since})")
                : string.Empty;
            var after = read.After > 0 ? string.Create(invariant, $" ({read.After} more after)") : string.Empty;
            output.TryAppendLine(string.Create(invariant, $"lines {first}-{last} of {read.Total}{skipped}{after}:"));
            foreach (var line in read.Lines)
            {
                output.TryAppendLine(string.Create(invariant, $"{line.Number,6} | {Cut(line.Text)}"));
            }

            nextPage = read.After > 0 ? last : null;
        }

        output.TryAppendLine();
        var scope = ScopeArg(terminal);
        if (nextPage is { } next)
        {
            var grepArg = grepHits is null ? string.Empty : $",\"grep\":\"{grep}\"";
            output.TryAppendLine(string.Create(invariant,
                $"Next page: cmd_terminal(action='read', name='{terminal.Name}'{scope}, optionsJson='{{\"since\":{next}{grepArg}}}')."));
        }

        output.TryAppendLine(grepHits is not null
            ? string.Create(invariant, $"A search does not move your cursor: it stays at line {cursor}.")
            : string.Create(invariant,
                $"cursor={cursor}. Next read of only new lines: cmd_terminal(action='read', name='{terminal.Name}'{scope}, optionsJson='{{\"since\":{Math.Max(cursor, read.Total)}}}')."));
        if (oldest > 1 && since + 1 < oldest)
        {
            output.TryAppendLine(string.Create(invariant, $"Lines before {oldest} are no longer stored: the log keeps only its newest files."));
        }

        return output.Build().TrimEnd();
    }

    /// <summary>The terminals of a scope, one line each.</summary>
    public static string List(IReadOnlyList<TerminalInfo> terminals, string scope, DateTimeOffset now, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(terminals);
        var output = new OutputBudget(maxChars);
        if (terminals.Count == 0)
        {
            output.TryAppendLine($"No terminals in {scope} yet. A run action (run, watch, node_run, py_run) or cmd_shell with 'terminal' creates one.");
            return output.Build().TrimEnd();
        }

        output.TryAppendLine(string.Create(CultureInfo.InvariantCulture, $"{terminals.Count} terminal(s) in {scope}:"));
        foreach (var info in terminals)
        {
            output.TryAppendLine("- " + Line(info, now));
        }

        output.TryAppendLine();
        output.TryAppendLine("Read one: cmd_terminal(action='read', name=...). Start one's last command again: action='rerun'. Stop: action='stop'.");
        return output.Build().TrimEnd();
    }

    /// <summary>One terminal in one line, as the guides and the list show it.</summary>
    public static string Line(TerminalInfo info, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(info);
        var t = info.Terminal;
        var parts = new List<string> { $"{t.Name} · {t.State.Word()}" };
        if (t.CommandLine is { Length: > 0 } command)
        {
            parts.Add($"`{Short(command)}`");
        }

        if (t.State.IsLive())
        {
            if (t.Urls.Count > 0)
            {
                parts.Add(t.Urls[0]);
            }

            if (t.StartedUtc is { } started)
            {
                parts.Add("up " + Span(now - started));
            }

            if (info.UnreadErrors > 0)
            {
                parts.Add(string.Create(CultureInfo.InvariantCulture, $"{info.UnreadErrors} unread error line(s)"));
            }

            if (info.PendingPrompt is not null)
            {
                parts.Add("waits for an answer");
            }
        }
        else if (t.EndedUtc is { } ended)
        {
            var who = t.StoppedBy switch
            {
                TerminalActors.Owner => " by the owner",
                TerminalActors.Claude => " by Claude",
                _ => string.Empty,
            };
            var code = t.ExitCode is { } exit && t.State == TerminalState.Crashed ? string.Create(CultureInfo.InvariantCulture, $" (exit {exit})") : string.Empty;
            parts.Add($"{t.State.Word()}{who}{code} {Span(now - ended)} ago");
        }

        if ((t.CurrentRunId ?? t.LastRunId) is { } runId)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"run #{runId}"));
        }

        return string.Join(" · ", parts);
    }

    /// <summary>A duration as people say it: 40s, 12m, 2h13m, 3d4h.</summary>
    public static string Span(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        return span.TotalMinutes < 1 ? string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalSeconds}s")
            : span.TotalHours < 1 ? string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalMinutes}m")
            : span.TotalDays < 1 ? string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalHours}h{span.Minutes:00}m")
            : string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalDays}d{span.Hours}h");
    }

    private static string Header(CommandRun run, Terminal? terminal, TerminalRuntime? runtime, DateTimeOffset now)
    {
        var name = terminal?.Name ?? "terminal";
        var id = run.Id.ToString(CultureInfo.InvariantCulture);
        if (run.Status == CommandRunStatus.Queued)
        {
            return $"⏳ Run #{id} {run.Operation} waits for its turn before it starts in terminal '{name}'.";
        }

        if (runtime?.Detector.IsReady == true)
        {
            var after = run.StartedUtc is { } started && terminal?.ReadyUtc is { } ready ? $" in {Span(ready - started)}" : string.Empty;
            var down = runtime.Health.AppDown ? " but the app inside it is down (it waits for a change)" : string.Empty;
            return $"🟢 Run #{id} {run.Operation} is up in terminal '{name}'{after}{down}: {runtime.Detector.How}. It keeps running until it is stopped.";
        }

        var since = run.StartedUtc is { } start ? " for " + Span(now - start) : string.Empty;
        return $"◌ Run #{id} {run.Operation} is starting in terminal '{name}'{since}; no sign that it is up yet. It keeps running; read it to follow along.";
    }

    private static string StateLine(Terminal terminal, TerminalRuntime? runtime, DateTimeOffset now)
    {
        var state = terminal.State.Word();
        var address = terminal.Urls.Count > 0 ? " · " + terminal.Urls[0] : string.Empty;
        var up = terminal.State.IsLive() && terminal.StartedUtc is { } started ? " · up " + Span(now - started) : string.Empty;
        var run = (terminal.CurrentRunId ?? terminal.LastRunId) is { } id ? string.Create(CultureInfo.InvariantCulture, $" · run #{id}") : string.Empty;
        var note = string.IsNullOrWhiteSpace(terminal.StateNote) ? string.Empty : " · " + terminal.StateNote;
        var live = runtime is null && terminal.State.IsLive() ? " (not running in this Waymark any more)" : string.Empty;
        return $"🖥 terminal '{terminal.Name}' ({terminal.Scope}) · {state}{live}{address}{up}{run} · `{Short(terminal.CommandLine ?? string.Empty)}`{note}";
    }

    private static string EndText(CommandRun run) => run.Status switch
    {
        CommandRunStatus.Cancelled => run.StoppedBy switch
        {
            TerminalActors.Claude => "stopped by Claude",
            TerminalActors.Waymark => "ended by Waymark",
            _ => "stopped by the owner",
        },
        CommandRunStatus.Succeeded => "the program ended by itself",
        CommandRunStatus.Failed => "the program ended with an error",
        CommandRunStatus.Denied => "not started",
        CommandRunStatus.TimedOut => "stopped after the most it may run",
        _ => run.Status.ToString().ToLowerInvariant(),
    };

    private static string NextSteps(string name, Terminal? terminal, long lines) =>
        string.Create(CultureInfo.InvariantCulture,
            $"Read new output: cmd_terminal(action='read', name='{name}'{ScopeArg(terminal)}, optionsJson='{{\"since\":{lines}}}'). " +
            $"Answer a prompt: action='send'. Start it again after a change it cannot reload: action='restart'. Stop it: action='stop'. Do not wait for it to end.");

    private static string ScopeArg(Terminal? terminal) =>
        terminal?.WorkspaceId is null ? string.Empty : $", workspace='{terminal.WorkspaceName}'";

    private static void AppendTail(OutputBudget output, string plainOutput, int lines)
    {
        var all = plainOutput.Split('\n').Select(l => l.TrimEnd('\r', ' ')).ToList();
        while (all.Count > 0 && all[^1].Length == 0)
        {
            all.RemoveAt(all.Count - 1);
        }

        output.TryAppendLine();
        if (all.Count == 0)
        {
            output.TryAppendLine("(no output yet)");
            return;
        }

        var tail = all.Skip(Math.Max(0, all.Count - lines)).ToList();
        output.TryAppendLine($"last output ({tail.Count} lines):");
        foreach (var line in tail)
        {
            output.TryAppendLine("  " + Cut(line));
        }
    }

    private static string Short(string commandLine)
    {
        var line = commandLine.ReplaceLineEndings(" ").Trim();
        var cut = line.IndexOf("   # runs:", StringComparison.Ordinal);
        if (cut > 0)
        {
            line = line[..cut];
        }

        return line.Length > 80 ? line[..80] + "..." : line;
    }

    private static string FirstLine(string commandLine)
    {
        var index = commandLine.IndexOf('\n', StringComparison.Ordinal);
        var line = index < 0 ? commandLine : commandLine[..index] + " (script continues)";
        return Cut(line.Trim());
    }

    private static string Cut(string text) => text.Length <= MaxLineChars ? text : text[..MaxLineChars] + "...";

    // Every line cut to the same width as a read shows, so a report of a program that printed huge lines stays readable.
    private static string CutLines(string text)
    {
        if (text.Length == 0)
        {
            return text;
        }

        var lines = text.Split('\n');
        return lines.Any(l => l.Length > MaxLineChars) ? string.Join('\n', lines.Select(Cut)) : text;
    }
}
