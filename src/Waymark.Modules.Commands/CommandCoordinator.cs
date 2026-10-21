using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Waymark.Core.Audit;
using Waymark.Core.Auth;
using Waymark.Core.Commands;
using Waymark.Core.Events;
using Waymark.Core.LiveFeed;
using Waymark.Modules.Commands.Execution;
using Waymark.Modules.Commands.Policy;

namespace Waymark.Modules.Commands;

/// <summary>
/// Owns every command run from the request to the end: asks the policy, waits for the owner when needed, takes the turn of the workspace,
/// starts the program, streams its output to the admin UI and stores the result. Approving is possible only through <see cref="ICommandRuns"/>,
/// which the admin UI uses; the tools Claude calls can only submit, wait and stop.
/// </summary>
public sealed class CommandCoordinator : ICommandRuns
{
    private const int MaxScriptChars = 20_000;
    private const int MaxAuditChars = 300;
    private const string OwnerName = AuthDefaults.AdminActor;
    private const int PreviewLines = 3;
    private const int PreviewLineChars = 160;
    private const int SummaryChars = 100;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(75);

    // Commands that edit project files themselves: when the files were approved before, the new state is approved too.
    private static readonly HashSet<string> BaselineRefreshing = new(StringComparer.Ordinal)
    {
        "dotnet.package_add", "dotnet.package_remove", "dotnet.reference", "dotnet.sln", "dotnet.new",
    };

    private readonly ICommandRunStore _runs;
    private readonly CommandPolicy _policy;
    private readonly IEventBus _events;
    private readonly IAuditStore _audit;
    private readonly TimeProvider _time;
    private readonly ProcessRunner _runner;
    private readonly ExecutableLocator _executables;
    private readonly CommandsOptions _options;
    private readonly ILogger<CommandCoordinator> _logger;
    private readonly LiveActivity _activity;
    private readonly ConcurrentDictionary<long, ActiveRun> _active = new();
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _workspaceLocks = new();
    private readonly SemaphoreSlim _global;

    public CommandCoordinator(
        ICommandRunStore runs,
        CommandPolicy policy,
        IEventBus events,
        IAuditStore audit,
        TimeProvider time,
        ProcessRunner runner,
        ExecutableLocator executables,
        CommandsOptions options,
        LiveActivity activity,
        ILogger<CommandCoordinator> logger)
    {
        _runs = runs ?? throw new ArgumentNullException(nameof(runs));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _executables = executables ?? throw new ArgumentNullException(nameof(executables));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _activity = activity ?? throw new ArgumentNullException(nameof(activity));
        _global = new SemaphoreSlim(options.MaxParallelRuns, options.MaxParallelRuns);
    }

    // ---- for the tools ----

    public async Task<SubmitResult> SubmitAsync(CommandSubmission submission, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(submission);
        var now = _time.GetUtcNow();
        var outcome = await _policy.EvaluateAsync(submission.Operation, submission.Workspace, BaselineRefreshing.Contains(submission.Operation.Key), ct).ConfigureAwait(false);
        if (outcome.Gate == CommandGate.Off)
        {
            var where = submission.Workspace is null ? string.Empty : $" for workspace '{submission.WorkspaceName}'";
            var reason = $"'{submission.Operation.Label}' is switched off{where}. The owner can change this on the Commands page of the admin UI.";
            var created = await _runs.CreateAsync(ToNew(submission, CommandRunStatus.Denied, null, now), ct).ConfigureAwait(false);
            await _runs.UpdateAsync(created.Id, new CommandRunUpdate { EndedUtc = now, Note = reason }, ct).ConfigureAwait(false);
            var refused = await _runs.GetAsync(created.Id, ct).ConfigureAwait(false) ?? created;
            await _events.PublishAsync(new CommandRunChanged(refused.Id, submission.Workspace?.Id, CommandRunStatus.Denied)).ConfigureAwait(false);
            _logger.LogCommand(LogLevel.Warning, refused.Id, "refused", submission.WorkspaceName, reason);
            return new SubmitResult(refused, reason);
        }

        var status = outcome.Gate == CommandGate.Ask ? CommandRunStatus.WaitingApproval : CommandRunStatus.Queued;
        var record = await _runs.CreateAsync(ToNew(submission, status, outcome.AskReason, now), ct).ConfigureAwait(false);
        var run = new ActiveRun(record.Id, submission, outcome, status);
        _active[record.Id] = run;
        run.Preview = new OutputPreview(PreviewLines, PreviewLineChars, line => _logger.LogCommand(LogLevel.Information, record.Id, "out", submission.WorkspaceName, line));
        run.LiveItem = _activity.Begin(LogKind.Command, ActivityLabel(record.Id, submission, status));
        _logger.LogCommand(LogLevel.Information, record.Id, StageOf(status), submission.WorkspaceName, Summary(submission));
        await _events.PublishAsync(new CommandRunChanged(record.Id, submission.Workspace?.Id, status)).ConfigureAwait(false);
        _ = Task.Run(() => ExecuteAsync(run), CancellationToken.None);
        return new SubmitResult(record, null);
    }

    /// <summary>Waits until the run ends or the time is up, then returns its stored state. Null when the run does not exist.</summary>
    public async Task<CommandRun?> WaitAsync(long runId, TimeSpan wait, CancellationToken ct)
    {
        if (_active.TryGetValue(runId, out var run) && wait > TimeSpan.Zero)
        {
            try
            {
                await run.Done.Task.WaitAsync(wait, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Still running: the caller reports the current state.
            }
        }

        return await _runs.GetAsync(runId, ct).ConfigureAwait(false);
    }

    /// <summary>The whole output as plain text (no colours, progress lines reduced to their last state). Null when the run does not exist.</summary>
    public async Task<string?> ReadPlainOutputAsync(long runId, CancellationToken ct)
    {
        var snapshot = await GetOutputAsync(runId, ct).ConfigureAwait(false);
        return snapshot is null ? null : AnsiText.Plain(snapshot.Text);
    }

    // ---- for the admin UI ----

    public Task<IReadOnlyList<CommandRun>> ListAsync(CommandRunQuery query, CancellationToken ct) => _runs.ListAsync(query, ct);

    public async Task<IReadOnlyList<CommandRun>> ListPendingAsync(CancellationToken ct)
    {
        var active = await _runs.ListActiveAsync(ct).ConfigureAwait(false);
        return active.Where(r => r.Status == CommandRunStatus.WaitingApproval).ToList();
    }

    public Task<CommandRun?> GetAsync(long id, CancellationToken ct) => _runs.GetAsync(id, ct);

    public async Task<RunOutputSnapshot?> GetOutputAsync(long runId, CancellationToken ct)
    {
        if (_active.TryGetValue(runId, out var run))
        {
            var (text, seq) = run.Output.Snapshot();
            return new RunOutputSnapshot(text, seq, IsLive: true);
        }

        var record = await _runs.GetAsync(runId, ct).ConfigureAwait(false);
        if (record is null)
        {
            return null;
        }

        if (record.OutputPath is null || !File.Exists(record.OutputPath))
        {
            return new RunOutputSnapshot(record.OutputBytes > 0 ? "(The output of this run is no longer stored.)\n" : string.Empty, 0, IsLive: false);
        }

        var stored = await File.ReadAllTextAsync(record.OutputPath, Encoding.UTF8, ct).ConfigureAwait(false);
        return new RunOutputSnapshot(stored, 0, IsLive: false);
    }

    public async Task<bool> DecideAsync(long runId, bool approve, string? reason, string? editedScript, CancellationToken ct)
    {
        if (!_active.TryGetValue(runId, out var run) || run.Status != CommandRunStatus.WaitingApproval)
        {
            return false;
        }

        string? edited = null;
        if (editedScript is not null)
        {
            if (run.Submission.Tier != CommandTier.Shell)
            {
                throw new ArgumentException("Only a shell script can be edited before it runs.", nameof(editedScript));
            }

            var trimmed = editedScript.Trim();
            if (trimmed.Length == 0 || trimmed.Length > MaxScriptChars)
            {
                throw new ArgumentException($"The script must have 1 to {MaxScriptChars} characters.", nameof(editedScript));
            }

            edited = string.Equals(trimmed, run.Submission.Script?.Trim(), StringComparison.Ordinal) ? null : trimmed;
        }

        var cleanReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (!run.Decision.TrySetResult(new Decision(approve, cleanReason, approve ? edited : null)))
        {
            return false;
        }

        _logger.LogCommand(
            approve ? LogLevel.Information : LogLevel.Warning,
            runId,
            approve ? "approved" : "denied",
            run.Submission.WorkspaceName,
            edited is not null ? "script edited by you" : cleanReason ?? string.Empty);
        await AuditDecisionAsync(run, approve, edited is not null, cleanReason).ConfigureAwait(false);
        return true;
    }

    public Task<bool> StopAsync(long runId, CancellationToken ct)
    {
        if (!_active.TryGetValue(runId, out var run))
        {
            return Task.FromResult(false);
        }

        run.Decision.TrySetResult(new Decision(false, "Stopped by the owner.", null, Stopped: true));
        run.Stop.Cancel();
        return Task.FromResult(true);
    }

    // ---- the life of one run ----

    private async Task ExecuteAsync(ActiveRun run)
    {
        RunOutcome outcome;
        try
        {
            outcome = await RunAsync(run).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            outcome = new RunOutcome(CommandRunStatus.Cancelled, null, "Stopped by the owner.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Command run {RunId} failed unexpectedly.", run.Id);
            outcome = new RunOutcome(CommandRunStatus.Failed, null, "Waymark could not run this command: " + ex.Message);
        }

        await FinishAsync(run, outcome).ConfigureAwait(false);
    }

    private async Task<RunOutcome> RunAsync(ActiveRun run)
    {
        var submission = run.Submission;
        if (run.Status == CommandRunStatus.WaitingApproval)
        {
            var decision = await AwaitDecisionAsync(run).ConfigureAwait(false);
            if (!decision.Approve)
            {
                var status = decision.Stopped ? CommandRunStatus.Cancelled : CommandRunStatus.Denied;
                return new RunOutcome(status, null, decision.Reason ?? "Denied.");
            }

            await ApplyApprovalAsync(run, decision).ConfigureAwait(false);
        }

        var workspaceLock = _workspaceLocks.GetOrAdd(submission.Workspace?.Id ?? -1, _ => new SemaphoreSlim(1, 1));
        var holdsWorkspace = false;
        var holdsGlobal = false;
        try
        {
            if (submission.Operation.Mutating)
            {
                await AcquireAsync(workspaceLock, run).ConfigureAwait(false);
                holdsWorkspace = true;
            }

            await AcquireAsync(_global, run).ConfigureAwait(false);
            holdsGlobal = true;

            var drift = await FindDriftAsync(run).ConfigureAwait(false);
            if (drift is not null)
            {
                return new RunOutcome(CommandRunStatus.Failed, null, drift);
            }

            await SetStatusAsync(run, CommandRunStatus.Running, new CommandRunUpdate { StartedUtc = _time.GetUtcNow() }).ConfigureAwait(false);
            var result = await RunProgramAsync(run).ConfigureAwait(false);
            if (result.Status == CommandRunStatus.Succeeded)
            {
                await RefreshBaselineAsync(run).ConfigureAwait(false);
            }

            return result;
        }
        finally
        {
            if (holdsGlobal)
            {
                _global.Release();
            }

            if (holdsWorkspace)
            {
                workspaceLock.Release();
            }
        }
    }

    private async Task<Decision> AwaitDecisionAsync(ActiveRun run)
    {
        using var timeout = new CancellationTokenSource(_options.ApprovalTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, run.Stop.Token);
        try
        {
            return await run.Decision.Task.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (run.Stop.IsCancellationRequested)
            {
                return new Decision(false, "Stopped by the owner.", null, Stopped: true);
            }

            var minutes = (int)_options.ApprovalTimeout.TotalMinutes;
            return new Decision(false, string.Create(CultureInfo.InvariantCulture, $"Nobody decided within {minutes} minutes, so the request expired."), null);
        }
    }

    private async Task ApplyApprovalAsync(ActiveRun run, Decision decision)
    {
        var update = new CommandRunUpdate { Status = CommandRunStatus.Queued, DecidedUtc = _time.GetUtcNow(), DecidedBy = OwnerName, Note = decision.Reason };
        if (decision.EditedScript is not null)
        {
            run.Script = decision.EditedScript;
            update = update with { CommandLine = decision.EditedScript, OriginalCommand = run.Submission.Script };
        }

        await SetStatusAsync(run, CommandRunStatus.Queued, update).ConfigureAwait(false);

        // The owner approved the project and git files as they were shown: later commands trust that state.
        if (run.Fingerprint is not null && run.Submission.Workspace is { } workspace)
        {
            await _policy.TrustAsync(workspace.Id, run.Fingerprint, _time.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task AcquireAsync(SemaphoreSlim gate, ActiveRun run)
    {
        if (await gate.WaitAsync(0).ConfigureAwait(false))
        {
            return;
        }

        if (run.Status != CommandRunStatus.Queued)
        {
            await SetStatusAsync(run, CommandRunStatus.Queued, new CommandRunUpdate()).ConfigureAwait(false);
        }

        await gate.WaitAsync(run.Stop.Token).ConfigureAwait(false);
    }

    private async Task<string?> FindDriftAsync(ActiveRun run)
    {
        if (run.Fingerprint is null || run.Submission.Workspace is not { } workspace || !run.Submission.Operation.DriftSensitive)
        {
            return null;
        }

        var current = await BuildFingerprint.ComputeAsync(workspace.Path, run.Stop.Token).ConfigureAwait(false);
        var changed = BuildFingerprint.Differences(run.Fingerprint, current);
        return changed.Count == 0
            ? null
            : "Project or git files changed after this command was requested (" + string.Join(", ", changed.Take(6)) + "). Request it again so it can be checked against the new state.";
    }

    private async Task RefreshBaselineAsync(ActiveRun run)
    {
        if (!run.IsClean || !BaselineRefreshing.Contains(run.Submission.Operation.Key) || run.Submission.Workspace is not { } workspace)
        {
            return;
        }

        var current = await BuildFingerprint.ComputeAsync(workspace.Path, CancellationToken.None).ConfigureAwait(false);
        await _policy.TrustAsync(workspace.Id, current, _time.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<RunOutcome> RunProgramAsync(ActiveRun run)
    {
        var submission = run.Submission;
        if (!Directory.Exists(submission.WorkingDirectory))
        {
            return new RunOutcome(CommandRunStatus.Failed, null, $"The folder '{submission.WorkingDirectory}' does not exist.");
        }

        using var flushStop = new CancellationTokenSource();
        var flusher = FlushLoopAsync(run, flushStop.Token);
        var clock = Stopwatch.StartNew();
        try
        {
            var outcome = submission.Tier == CommandTier.Shell
                ? await RunShellAsync(run).ConfigureAwait(false)
                : await RunRecipeAsync(run).ConfigureAwait(false);
            run.Elapsed = clock.Elapsed;
            run.Output.Append(Footer(outcome, clock.Elapsed));
            return outcome;
        }
        finally
        {
            await flushStop.CancelAsync().ConfigureAwait(false);
            await flusher.ConfigureAwait(false);
        }
    }

    private async Task<RunOutcome> RunRecipeAsync(ActiveRun run)
    {
        var submission = run.Submission;
        var plan = submission.Plan ?? throw new InvalidOperationException("A recipe run has no plan.");
        run.Output.Append($"\u001b[90m{submission.WorkingDirectory}\u001b[0m \u001b[32m$\u001b[0m {plan.CommandLine}\n");

        if (plan.InProcess is { } action)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(run.Stop.Token);
            limit.CancelAfter(submission.Timeout);
            try
            {
                var code = await action(run.Append, limit.Token).ConfigureAwait(false);
                return code == 0 ? new RunOutcome(CommandRunStatus.Succeeded, code, null) : new RunOutcome(CommandRunStatus.Failed, code, null);
            }
            catch (OperationCanceledException)
            {
                return run.Stop.IsCancellationRequested
                    ? new RunOutcome(CommandRunStatus.Cancelled, null, "Stopped by the owner.")
                    : new RunOutcome(CommandRunStatus.TimedOut, null, TimedOutNote(submission.Timeout));
            }
        }

        var program = _executables.Find(plan.Program);
        if (program is null)
        {
            return new RunOutcome(CommandRunStatus.Failed, null, $"'{plan.Program}' was not found in the folders of PATH on this computer.");
        }

        var spec = new RunSpec(program, plan.Arguments, submission.WorkingDirectory, ChildEnvironment.Build(), submission.Timeout);
        return ToOutcome(await _runner.RunAsync(spec, run.Append, run.Stop.Token).ConfigureAwait(false), submission.Timeout);
    }

    private async Task<RunOutcome> RunShellAsync(ActiveRun run)
    {
        var submission = run.Submission;
        var shell = submission.Shell ?? throw new InvalidOperationException("A shell run has no shell.");
        var script = run.Script ?? throw new InvalidOperationException("A shell run has no script.");
        run.Output.Append($"\u001b[90m{submission.WorkingDirectory}\u001b[0m \u001b[32m$\u001b[0m \u001b[90m# {shell.DisplayName}\u001b[0m\n");
        run.Output.Append(script.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n\u001b[90m# output\u001b[0m\n");

        using var file = await ShellScripts.WriteAsync(shell, script, run.Stop.Token).ConfigureAwait(false);
        var spec = new RunSpec(shell.FileName, ShellScripts.Arguments(shell, file.Path), submission.WorkingDirectory, ChildEnvironment.Build(), submission.Timeout);
        return ToOutcome(await _runner.RunAsync(spec, run.Append, run.Stop.Token).ConfigureAwait(false), submission.Timeout);
    }

    private static RunOutcome ToOutcome(RunResult result, TimeSpan timeout)
    {
        if (result.StartFailure is not null)
        {
            return new RunOutcome(CommandRunStatus.Failed, null, result.StartFailure);
        }

        if (result.Cancelled)
        {
            return new RunOutcome(CommandRunStatus.Cancelled, result.ExitCode, "Stopped by the owner.");
        }

        if (result.TimedOut)
        {
            return new RunOutcome(CommandRunStatus.TimedOut, result.ExitCode, TimedOutNote(timeout));
        }

        return result.ExitCode == 0
            ? new RunOutcome(CommandRunStatus.Succeeded, 0, null)
            : new RunOutcome(CommandRunStatus.Failed, result.ExitCode, null);
    }

    private static string TimedOutNote(TimeSpan timeout) => string.Create(CultureInfo.InvariantCulture, $"Stopped after the time limit of {timeout.TotalMinutes:0.#} minutes.");

    private static string Footer(RunOutcome outcome, TimeSpan elapsed)
    {
        var colour = outcome.Status == CommandRunStatus.Succeeded ? "32" : "31";
        var code = outcome.ExitCode is { } value ? string.Create(CultureInfo.InvariantCulture, $"exit code {value}") : outcome.Status.ToString().ToLowerInvariant();
        return string.Create(CultureInfo.InvariantCulture, $"\n\u001b[{colour}m[{code} - {elapsed.TotalSeconds:0.0}s]\u001b[0m\n");
    }

    private async Task FlushLoopAsync(ActiveRun run, CancellationToken stop)
    {
        using var timer = new PeriodicTimer(FlushInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stop).ConfigureAwait(false))
            {
                await PublishPendingAsync(run).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The run ended: the last output is published below.
        }

        await PublishPendingAsync(run).ConfigureAwait(false);
    }

    private async Task PublishPendingAsync(ActiveRun run)
    {
        if (run.Output.Flush() is { } chunk)
        {
            await _events.PublishAsync(new CommandOutputChunk(run.Id, run.Submission.Workspace?.Id, chunk.Seq, chunk.Text)).ConfigureAwait(false);
        }
    }

    private async Task SetStatusAsync(ActiveRun run, CommandRunStatus status, CommandRunUpdate update)
    {
        await _runs.UpdateAsync(run.Id, update with { Status = status }, CancellationToken.None).ConfigureAwait(false);
        run.Status = status;
        run.LiveItem?.Update(ActivityLabel(run.Id, run.Submission, status));
        if (status == CommandRunStatus.Running)
        {
            _logger.LogCommand(LogLevel.Information, run.Id, "started", run.Submission.WorkspaceName, string.Empty);
        }

        await _events.PublishAsync(new CommandRunChanged(run.Id, run.Submission.Workspace?.Id, status)).ConfigureAwait(false);
    }

    private async Task FinishAsync(ActiveRun run, RunOutcome outcome)
    {
        try
        {
            run.Preview?.Complete();
            _logger.LogCommand(LevelOf(outcome.Status), run.Id, StageOf(outcome.Status), run.Submission.WorkspaceName, FinishDetail(run, outcome));
            await PublishPendingAsync(run).ConfigureAwait(false);
            var (text, _) = run.Output.Snapshot();
            string? path = null;
            var note = outcome.Note;
            if (text.Length > 0)
            {
                path = Path.Combine(_options.RunsDirectory, string.Create(CultureInfo.InvariantCulture, $"run-{run.Id}.log"));
                try
                {
                    Directory.CreateDirectory(_options.RunsDirectory);
                    await File.WriteAllTextAsync(path, text, new UTF8Encoding(false), CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(ex, "The output of command run {RunId} could not be stored.", run.Id);
                    path = null;
                    note = (note is null ? string.Empty : note + " ") + "The output could not be stored.";
                }
            }

            var update = new CommandRunUpdate
            {
                Status = outcome.Status,
                ExitCode = outcome.ExitCode,
                EndedUtc = _time.GetUtcNow(),
                Note = note,
                OutputBytes = Encoding.UTF8.GetByteCount(text),
                OutputPath = path,
            };
            await _runs.UpdateAsync(run.Id, update, CancellationToken.None).ConfigureAwait(false);
            run.Status = outcome.Status;
            _active.TryRemove(run.Id, out _);
            await _events.PublishAsync(new CommandRunChanged(run.Id, run.Submission.Workspace?.Id, outcome.Status)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Command run {RunId} could not be finished in the store.", run.Id);
            _active.TryRemove(run.Id, out _);
        }
        finally
        {
            run.LiveItem?.Dispose();
            run.Done.TrySetResult();
        }
    }

    private async Task AuditDecisionAsync(ActiveRun run, bool approve, bool edited, string? reason)
    {
        try
        {
            var line = run.Submission.CommandLine;
            var shown = line.Length > MaxAuditChars ? line[..MaxAuditChars] + "..." : line;
            var details = $"{run.Submission.Operation.Key}: {shown}{(edited ? " [edited by the owner]" : string.Empty)}";
            var entry = AuditEntries.Create(
                _time,
                AuditCategory.Approval,
                approve ? "command.approve" : "command.deny",
                OwnerName,
                run.Submission.WorkspaceName,
                approve ? AuditOutcome.Ok : AuditOutcome.Denied,
                details,
                reason);
            await _audit.AppendAsync(entry, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The decision on command run {RunId} could not be written to the audit log.", run.Id);
        }
    }

    private static string StageOf(CommandRunStatus status) => status switch
    {
        CommandRunStatus.WaitingApproval => "waiting for you",
        CommandRunStatus.Queued => "queued",
        CommandRunStatus.Running => "running",
        CommandRunStatus.Succeeded => "succeeded",
        CommandRunStatus.Failed => "failed",
        CommandRunStatus.Denied => "denied",
        CommandRunStatus.TimedOut => "timed out",
        CommandRunStatus.Cancelled => "stopped",
        _ => status.ToString().ToLowerInvariant()
    };

    // A failed or timed out run is an error on the Live page; a denied or stopped one is a warning.
    private static LogLevel LevelOf(CommandRunStatus status) => status switch
    {
        CommandRunStatus.Succeeded => LogLevel.Information,
        CommandRunStatus.Failed or CommandRunStatus.TimedOut => LogLevel.Error,
        _ => LogLevel.Warning
    };

    private static string Summary(CommandSubmission submission)
    {
        var line = submission.CommandLine.ReplaceLineEndings(" ").Trim();
        var shown = line.Length <= SummaryChars ? line : line[..SummaryChars] + "...";
        return submission.Tier == CommandTier.Shell ? "shell: " + shown : shown;
    }

    private static string ActivityLabel(long id, CommandSubmission submission, CommandRunStatus status) =>
        string.Create(CultureInfo.InvariantCulture, $"#{id} {submission.Operation.Label} in {submission.WorkspaceName}: {StageOf(status)}");

    private static string FinishDetail(ActiveRun run, RunOutcome outcome)
    {
        var parts = new List<string>(3);
        if (outcome.ExitCode is { } code)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"exit {code}"));
        }

        if (run.Elapsed > TimeSpan.Zero)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{run.Elapsed.TotalSeconds:0.0}s"));
        }

        if (!string.IsNullOrWhiteSpace(outcome.Note))
        {
            parts.Add(outcome.Note);
        }

        return string.Join("  ", parts);
    }

    private static NewCommandRun ToNew(CommandSubmission s, CommandRunStatus status, string? askReason, DateTimeOffset now) =>
        new(
            s.Workspace?.Id,
            s.WorkspaceName,
            s.Tier,
            s.Operation.Key,
            s.Shell?.Id,
            s.CommandLine,
            s.WorkingDirectory,
            s.Purpose,
            status,
            s.Actor,
            askReason,
            s.Risks,
            now);

    private sealed record RunOutcome(CommandRunStatus Status, int? ExitCode, string? Note);

    private sealed record Decision(bool Approve, string? Reason, string? EditedScript, bool Stopped = false);

    private sealed class ActiveRun
    {
        public ActiveRun(long id, CommandSubmission submission, PolicyOutcome policy, CommandRunStatus status)
        {
            Id = id;
            Submission = submission;
            Fingerprint = policy.Fingerprint;
            IsClean = policy.IsClean;
            Script = submission.Script;
            Status = status;
        }

        public long Id { get; }

        public CommandSubmission Submission { get; }

        public IReadOnlyDictionary<string, string>? Fingerprint { get; }

        public bool IsClean { get; }

        public string? Script { get; set; }

        public volatile CommandRunStatus Status;

        public RunOutput Output { get; } = new();

        /// <summary>The line of this run in the Live page's "happening now" list. Ends with the run.</summary>
        public ActivityScope? LiveItem { get; set; }

        /// <summary>Picks the first lines of the program's output for the Live page.</summary>
        public OutputPreview? Preview { get; set; }

        /// <summary>How long the program ran; zero when it never started.</summary>
        public TimeSpan Elapsed { get; set; }

        /// <summary>Takes the output of the program: it goes to the run's output and, for the first lines, to the Live page.</summary>
        public void Append(string text)
        {
            Output.Append(text);
            Preview?.Add(text);
        }

        public CancellationTokenSource Stop { get; } = new();

        public TaskCompletionSource<Decision> Decision { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
