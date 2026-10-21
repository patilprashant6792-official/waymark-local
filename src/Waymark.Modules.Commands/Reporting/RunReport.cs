using System.Globalization;
using Waymark.Core.Commands;
using Waymark.Core.Tools;
using Waymark.Modules.Commands.Execution;

namespace Waymark.Modules.Commands.Reporting;

/// <summary>
/// What Claude reads after a command: the result first, then the compiler errors and failed tests found in the output, then the last lines.
/// The full output stays one call away (cmd_output), so a build that prints thousands of lines costs a short answer.
/// </summary>
public static class RunReport
{
    private const int MaxErrors = 40;
    private const int MaxWarnings = 15;
    private const int MaxLineChars = 300;
    private const int TailOnFailure = 60;
    private const int TailOnSuccess = 20;
    private const int TailWhileRunning = 25;

    public static string Build(CommandRun run, string plainOutput, string? workspaceRoot, DateTimeOffset now, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(run);
        var output = new OutputBudget(maxChars);
        output.TryAppendLine(Header(run, now));
        output.TryAppendLine($"command: {FirstLine(run.CommandLine)}");
        output.TryAppendLine($"folder: {run.WorkingDirectory}");
        if (run.Purpose.Length > 0)
        {
            output.TryAppendLine($"purpose: {run.Purpose}");
        }

        if (!string.IsNullOrWhiteSpace(run.Note))
        {
            output.TryAppendLine($"note: {run.Note}");
        }

        if (run.Status == CommandRunStatus.WaitingApproval)
        {
            AppendWaiting(output, run);
        }
        else if (run.Status.IsActive())
        {
            output.TryAppendLine();
            output.TryAppendLine("The command is still going. cmd_wait(runId) waits for it; cmd_output(runId) reads what it printed; cmd_stop(runId) ends it.");
            AppendTail(output, plainOutput, TailWhileRunning, "last output so far");
        }
        else
        {
            AppendFinished(output, run, plainOutput, workspaceRoot);
        }

        return output.Build().TrimEnd();
    }

    private static string Header(CommandRun run, DateTimeOffset now)
    {
        var id = run.Id.ToString(CultureInfo.InvariantCulture);
        var duration = Duration(run);
        return run.Status switch
        {
            CommandRunStatus.Succeeded => $"✅ Run #{id} {run.Operation} succeeded (exit code {run.ExitCode ?? 0}){duration}",
            CommandRunStatus.Failed => run.ExitCode is { } code ? $"❌ Run #{id} {run.Operation} failed (exit code {code}){duration}" : $"❌ Run #{id} {run.Operation} failed{duration}",
            CommandRunStatus.TimedOut => $"⏱ Run #{id} {run.Operation} timed out{duration}",
            CommandRunStatus.Cancelled => $"🛑 Run #{id} {run.Operation} was stopped{duration}",
            CommandRunStatus.Denied => $"⛔ Run #{id} {run.Operation} was not allowed",
            CommandRunStatus.WaitingApproval => $"⏳ Run #{id} {run.Operation} waits for the owner's approval ({Ago(now - run.RequestedUtc)})",
            CommandRunStatus.Queued => $"⏳ Run #{id} {run.Operation} is queued behind another command in this workspace",
            _ => $"🔄 Run #{id} {run.Operation} is running ({Ago(now - (run.StartedUtc ?? run.RequestedUtc))})",
        };
    }

    private static void AppendWaiting(OutputBudget output, CommandRun run)
    {
        output.TryAppendLine();
        output.TryAppendLine("Nothing has run. The owner decides in the Waymark admin UI (Console page, or the notice on the dashboard); you cannot approve it yourself.");
        if (!string.IsNullOrWhiteSpace(run.AskReason))
        {
            output.TryAppendLine($"Why it asks: {run.AskReason.Replace('\n', ' ')}");
        }

        if (run.Risks.Count > 0)
        {
            output.TryAppendLine($"Flagged: {string.Join("; ", run.Risks)}");
        }

        output.TryAppendLine("cmd_wait(runId) waits for the decision; cmd_stop(runId) withdraws the request.");
    }

    private static void AppendFinished(OutputBudget output, CommandRun run, string plainOutput, string? workspaceRoot)
    {
        var failed = run.Status != CommandRunStatus.Succeeded;
        if (run.Status == CommandRunStatus.Denied)
        {
            return;
        }

        var isDotnet = run.Operation.StartsWith("dotnet.", StringComparison.Ordinal);
        IReadOnlyList<BuildDiagnostic> diagnostics = isDotnet && workspaceRoot is not null
            ? DiagnosticsParser.ParseBuild(plainOutput, workspaceRoot)
            : Array.Empty<BuildDiagnostic>();
        var errors = diagnostics.Where(d => d.IsError).ToList();
        var warnings = diagnostics.Where(d => !d.IsError).ToList();
        if (errors.Count > 0)
        {
            output.TryAppendLine();
            output.TryAppendLine($"errors ({errors.Count}):");
            foreach (var error in errors.Take(MaxErrors))
            {
                output.TryAppendLine("  " + Describe(error));
            }

            if (errors.Count > MaxErrors)
            {
                output.TryAppendLine($"  ... and {errors.Count - MaxErrors} more (cmd_output(runId, grep=\"error\"))");
            }
        }

        if (warnings.Count > 0)
        {
            output.TryAppendLine();
            output.TryAppendLine($"warnings ({warnings.Count}):");
            foreach (var warning in warnings.Take(MaxWarnings))
            {
                output.TryAppendLine("  " + Describe(warning));
            }

            if (warnings.Count > MaxWarnings)
            {
                output.TryAppendLine($"  ... and {warnings.Count - MaxWarnings} more (cmd_output(runId, grep=\"warning\"))");
            }
        }

        if (run.Operation == "dotnet.test" && DiagnosticsParser.ParseTests(plainOutput) is { } tests)
        {
            output.TryAppendLine();
            output.TryAppendLine($"tests: {tests.Passed} passed, {tests.Failed} failed, {tests.Skipped} skipped, {tests.Total} total");
            foreach (var name in tests.FailedTests)
            {
                output.TryAppendLine($"  failed: {name}");
            }
        }

        AppendTail(output, plainOutput, failed && errors.Count == 0 ? TailOnFailure : failed ? 12 : TailOnSuccess, "end of the output");
        output.TryAppendLine();
        output.TryAppendLine(NextStep(run, errors.Count));
    }

    private static string NextStep(CommandRun run, int errorCount)
    {
        if (run.Status == CommandRunStatus.Succeeded)
        {
            return $"Full output: cmd_output(runId={run.Id}).";
        }

        if (errorCount > 0)
        {
            return "Fix the errors above, then run the same action again. Full output: cmd_output(runId, grep=...).";
        }

        return $"Read more with cmd_output(runId={run.Id}, fromLine=..., lines=...) or search it with grep.";
    }

    private static void AppendTail(OutputBudget output, string plainOutput, int lines, string label)
    {
        var all = plainOutput.Split('\n').Select(l => l.TrimEnd('\r', ' ')).ToList();
        while (all.Count > 0 && all[^1].Length == 0)
        {
            all.RemoveAt(all.Count - 1);
        }

        if (all.Count == 0)
        {
            output.TryAppendLine();
            output.TryAppendLine("(no output yet)");
            return;
        }

        var tail = all.Skip(Math.Max(0, all.Count - lines)).ToList();
        output.TryAppendLine();
        output.TryAppendLine($"{label} ({tail.Count} of {all.Count} lines):");
        foreach (var line in tail)
        {
            output.TryAppendLine("  " + Cut(line));
        }
    }

    private static string Describe(BuildDiagnostic d)
    {
        var where = d.File is null ? string.Empty : d.Line > 0 ? $"{d.File}({d.Line}): " : $"{d.File}: ";
        return $"{where}{d.Code} {Cut(d.Message)}";
    }

    private static string Cut(string text) => text.Length <= MaxLineChars ? text : text[..MaxLineChars] + "...";

    private static string FirstLine(string commandLine)
    {
        var index = commandLine.IndexOf('\n', StringComparison.Ordinal);
        var line = index < 0 ? commandLine : commandLine[..index] + " (script continues)";
        return Cut(line.Trim());
    }

    private static string Duration(CommandRun run) =>
        run.StartedUtc is { } start && run.EndedUtc is { } end
            ? string.Create(CultureInfo.InvariantCulture, $" in {(end - start).TotalSeconds:0.0}s")
            : string.Empty;

    private static string Ago(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        return span.TotalMinutes >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalMinutes} min {span.Seconds}s ago")
            : string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalSeconds}s ago");
    }
}
