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
using Waymark.Core.Security;
using Waymark.Modules.Commands.Execution;
using Waymark.Modules.Commands.Policy;
using Waymark.Modules.Commands.Recipes;
using Waymark.Modules.Commands.Terminals;

namespace Waymark.Modules.Commands;

/// <summary>
/// Owns every command run from the request to the end: asks the policy, waits for the owner when needed, takes the turn of the workspace,
/// starts the program, streams its output to the admin UI and stores the result. Approving is possible only through <see cref="ICommandRuns"/>,
/// which the admin UI uses; the tools Claude calls can only submit, wait and stop.
/// A run in a terminal keeps its program running until it is stopped: it holds the workspace's turn and a run slot only while it builds and starts,
/// then hands them back, and its output is streamed to disk and watched for readiness, health and prompts while it runs.
/// </summary>
public sealed class CommandCoordinator : ICommandRuns
{
    private const int MaxScriptChars = 20_000;
    private const int MaxAuditChars = 300;
    private const int MaxInputChars = 4_000;
    private const string OwnerName = AuthDefaults.AdminActor;
    private const int PreviewLines = 3;
    private const int PreviewLineChars = 160;
    private const int SummaryChars = 100;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(75);
    private static readonly TimeSpan TerminalTick = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan PortPollStarting = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PortPollRunning = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PromptQuiet = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ShellReadyQuiet = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ErrorNoticeGap = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan OutputSaveGap = TimeSpan.FromSeconds(5);

    /// <summary>The line written into a terminal's output when Claude clears it; the Console shows only what follows the last one.</summary>
    public const string ClearedMarker = "──── cleared by Claude ────";

    // Commands that edit project files themselves: when the files were approved before, the new state is approved too.
    private static readonly HashSet<string> BaselineRefreshing = new(StringComparer.Ordinal)
    {
        "dotnet.package_add", "dotnet.package_remove", "dotnet.reference", "dotnet.sln", "dotnet.new",
    };

    private readonly ICommandRunStore _runs;
    private readonly ITerminalStore _terminals;
    private readonly CommandPolicy _policy;
    private readonly IEventBus _events;
    private readonly IAuditStore _audit;
    private readonly TimeProvider _time;
    private readonly ProcessRunner _runner;
    private readonly ProcessTree _tree;
    private readonly ListeningPorts _ports;
    private readonly ExecutableLocator _executables;
    private readonly CommandsOptions _options;
    private readonly ILogger<CommandCoordinator> _logger;
    private readonly LiveActivity _activity;
    private readonly ConcurrentDictionary<long, ActiveRun> _active = new();
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _workspaceLocks = new();
    private readonly SemaphoreSlim _global;
    private long _noticeSeq;

    public CommandCoordinator(
        ICommandRunStore runs,
        ITerminalStore terminals,
        CommandPolicy policy,
        IEventBus events,
        IAuditStore audit,
        TimeProvider time,
        ProcessRunner runner,
        ProcessTree tree,
        ListeningPorts ports,
        ExecutableLocator executables,
        CommandsOptions options,
        LiveActivity activity,
        ILogger<CommandCoordinator> logger)
    {
        _runs = runs ?? throw new ArgumentNullException(nameof(runs));
        _terminals = terminals ?? throw new ArgumentNullException(nameof(terminals));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _tree = tree ?? throw new ArgumentNullException(nameof(tree));
        _ports = ports ?? throw new ArgumentNullException(nameof(ports));
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
            _logger.LogCommand(LogLevel.Warning, refused.Id, "refused", submission.WorkspaceName, reason, submission.Purpose);
            return new SubmitResult(refused, reason);
        }

        // Starting the same terminal command again (the developer's up-arrow and Enter) reuses the owner's approval while nothing it depends on changed.
        var reused = false;
        if (outcome.Gate == CommandGate.Ask && submission.Terminal?.Prior is { } prior && ReusesApproval(prior, submission, outcome, now))
        {
            outcome = outcome with { Gate = CommandGate.Auto, AskReason = null };
            reused = true;
        }

        var status = outcome.Gate == CommandGate.Ask ? CommandRunStatus.WaitingApproval : CommandRunStatus.Queued;
        var record = await _runs.CreateAsync(ToNew(submission, status, outcome.AskReason, now), ct).ConfigureAwait(false);
        var run = new ActiveRun(record.Id, submission, outcome, status);
        if (submission.Terminal is { } launch)
        {
            var log = new TerminalLog(LogPathOf(record.Id), _options.TerminalLogMaxBytes, _options.TerminalLogFiles);
            run.Terminal = new TerminalRuntime(launch, log);
            await _terminals.UpdateAsync(launch.TerminalId, new TerminalUpdate
            {
                StartRun = true,
                State = TerminalState.Starting,
                CurrentRunId = record.Id,
                LastRunId = record.Id,
                Operation = submission.Operation.Key,
                CommandLine = submission.CommandLine,
                Folder = submission.WorkingDirectory,
                StartedUtc = now,
                StateNote = status == CommandRunStatus.WaitingApproval ? "Waiting for the owner's approval." : null,
            }, ct).ConfigureAwait(false);
            await PublishTerminalAsync(launch.TerminalId, submission, TerminalState.Starting).ConfigureAwait(false);
            if (reused)
            {
                await AuditAsync(AuditCategory.Tool, "terminal.approval_reused", submission.Actor, submission, $"terminal {launch.Name}: same command approved on {ApprovedOn(submission)}").ConfigureAwait(false);
                run.Append($"\u001b[90m# The owner approved this same command before and nothing it depends on changed, so it starts without asking.\u001b[0m\n");
            }
        }

        _active[record.Id] = run;
        run.Preview = new OutputPreview(PreviewLines, PreviewLineChars, line => _logger.LogCommand(LogLevel.Information, record.Id, "out", submission.WorkspaceName, line));
        run.LiveItem = _activity.Begin(LogKind.Command, ActivityLabel(record.Id, submission, status));
        _logger.LogCommand(LogLevel.Information, record.Id, StageOf(status), submission.WorkspaceName, Summary(submission), submission.Purpose);
        await _events.PublishAsync(new CommandRunChanged(record.Id, submission.Workspace?.Id, status)).ConfigureAwait(false);
        _ = Task.Run(() => ExecuteAsync(run), CancellationToken.None);
        return new SubmitResult(record, null);

        static string ApprovedOn(CommandSubmission s) =>
            s.Terminal?.Prior?.ApprovedUtc.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture) ?? "an earlier day";
    }

    /// <summary>
    /// Waits until the run ends or the time is up, then returns its stored state. A run in a terminal counts as done waiting once its program is up.
    /// Null when the run does not exist.
    /// </summary>
    public async Task<CommandRun?> WaitAsync(long runId, TimeSpan wait, CancellationToken ct)
    {
        if (_active.TryGetValue(runId, out var run) && wait > TimeSpan.Zero)
        {
            try
            {
                var done = run.Terminal is { } terminal
                    ? Task.WhenAny(run.Done.Task, terminal.Ready.Task)
                    : run.Done.Task;
                await done.WaitAsync(wait, ct).ConfigureAwait(false);
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

    /// <summary>The live side of a run in a terminal while it runs; null when the run is not active or not in a terminal.</summary>
    public TerminalRuntime? GetTerminalRuntime(long runId) =>
        _active.TryGetValue(runId, out var run) ? run.Terminal : null;

    /// <summary>True while the run is active (waiting, queued or running) in this Waymark.</summary>
    public bool IsActive(long runId) => _active.ContainsKey(runId);

    /// <summary>
    /// Types a line into the program of a run in a terminal and shows it in its output (marked as typed, secrets hidden). Null when it was written,
    /// otherwise why not.
    /// </summary>
    public async Task<string?> SendInputAsync(long runId, string text, bool enter, string actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!_active.TryGetValue(runId, out var run) || run.Terminal is not { } terminal)
        {
            return "Nothing runs in that terminal now.";
        }

        if (terminal.Process is not { } process || process.HasExited || process.IsStopping)
        {
            return "The program has not started yet, or it is ending.";
        }

        if (text.Length > MaxInputChars || text.Contains('\0', StringComparison.Ordinal))
        {
            return $"The text must have at most {MaxInputChars} characters and no NUL characters.";
        }

        var payload = enter ? text + "\n" : text;
        if (!await process.WriteAsync(payload, ct).ConfigureAwait(false))
        {
            return "The program does not read its input any more.";
        }

        var shown = SensitiveDataScanner.Redact(text).Text.ReplaceLineEndings(" ⏎ ");
        var who = actor == TerminalActors.Owner ? "You" : "Claude";
        run.Append($"\u001b[33m› {who} sent: {shown}{(enter ? " ⏎" : string.Empty)}\u001b[0m\n");
        terminal.Answered();
        await _terminals.UpdateAsync(terminal.Launch.TerminalId, new TerminalUpdate { LastOutputUtc = _time.GetUtcNow() }, CancellationToken.None).ConfigureAwait(false);
        await AuditAsync(AuditCategory.Tool, "terminal.input", actor, run.Submission, $"terminal {terminal.Launch.Name}: {Cut(shown)}").ConfigureAwait(false);
        return null;
    }

    /// <summary>Clears what a terminal shows (the Console starts after a marker line) and moves Claude's read cursor to the end. False when nothing runs.</summary>
    public bool ClearTerminalView(long runId)
    {
        if (!_active.TryGetValue(runId, out var run) || run.Terminal is not { } terminal)
        {
            return false;
        }

        run.Append($"\u001b[90m{ClearedMarker}\u001b[0m\n");
        terminal.MarkRead(terminal.Log.LineCount);
        return true;
    }

    /// <summary>Stops every terminal program of one workspace (null: the system terminals). Returns how many were asked to stop.</summary>
    public async Task<int> StopTerminalsAsync(long? workspaceId, bool allScopes, string actor, string? note, CancellationToken ct)
    {
        var targets = _active.Values
            .Where(run => run.Terminal is not null && (allScopes || run.Submission.Workspace?.Id == workspaceId))
            .ToList();
        foreach (var run in targets)
        {
            run.StopNote = note;
            await StopAsync(run.Id, actor, ct).ConfigureAwait(false);
        }

        return targets.Count;
    }

    /// <summary>Waits until every listed run ended, or the time is up.</summary>
    public async Task WaitEndedAsync(IEnumerable<long> runIds, TimeSpan wait)
    {
        var tasks = runIds.Select(id => _active.TryGetValue(id, out var run) ? run.Done.Task : Task.CompletedTask).ToList();
        try
        {
            await Task.WhenAll(tasks).WaitAsync(wait).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Shutting down: what did not end in time is ended with Waymark.
        }
    }

    /// <summary>The ids of the active runs that are in a terminal.</summary>
    public IReadOnlyList<long> ActiveTerminalRuns() => _active.Values.Where(run => run.Terminal is not null).Select(run => run.Id).ToList();

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

        var stored = await ReadSharedAsync(record.OutputPath, ct).ConfigureAwait(false);
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
            edited is not null ? "script edited by you" : cleanReason ?? string.Empty,
            run.Submission.Purpose);
        await AuditDecisionAsync(run, approve, edited is not null, cleanReason).ConfigureAwait(false);
        return true;
    }

    /// <summary>Ends a run that waits, is queued or runs, on the owner's behalf (the admin UI). False when it is not active.</summary>
    public Task<bool> StopAsync(long runId, CancellationToken ct) => StopAsync(runId, TerminalActors.Owner, ct);

    /// <summary>
    /// Ends a run that waits, is queued or runs. A program running in a terminal is stopped as a developer stops it (Ctrl+C, then terminate, then
    /// kill); anything else is ended at once with its process tree. <paramref name="actor"/> is recorded and audited. False when it is not active.
    /// </summary>
    public async Task<bool> StopAsync(long runId, string actor, CancellationToken ct)
    {
        if (!_active.TryGetValue(runId, out var run))
        {
            return false;
        }

        var first = run.StopActor is null;
        run.StopActor ??= actor;
        run.Decision.TrySetResult(new Decision(false, StopNote(run), null, Stopped: true));
        await run.Stop.CancelAsync().ConfigureAwait(false);
        if (first)
        {
            _logger.LogCommand(LogLevel.Warning, runId, "stopping", run.Submission.WorkspaceName, StopNote(run), run.Submission.Purpose);
            await AuditAsync(actor == TerminalActors.Claude ? AuditCategory.Tool : AuditCategory.Approval, run.Terminal is null ? "command.stop" : "terminal.stop", actor, run.Submission, Cut(run.Submission.CommandLine)).ConfigureAwait(false);
        }

        return true;
    }

    public async Task<bool> DeleteAsync(long runId, CancellationToken ct)
    {
        // A run the coordinator still holds is finishing right now; it can be deleted once it has finished.
        if (_active.ContainsKey(runId))
        {
            return false;
        }

        var deleted = await _runs.DeleteFinishedAsync(runId, ct).ConfigureAwait(false);
        if (deleted is null)
        {
            return false;
        }

        RemoveStoredOutput(deleted);
        _logger.LogInformation("The owner deleted command run {RunId} from the history.", deleted.Id);
        await AuditHistoryAsync("command.delete", deleted.WorkspaceName, string.Create(CultureInfo.InvariantCulture, $"run #{deleted.Id} {deleted.Operation}: {Cut(deleted.CommandLine)}")).ConfigureAwait(false);
        await _events.PublishAsync(new CommandRunsDeleted(deleted.WorkspaceId, [deleted.Id])).ConfigureAwait(false);
        return true;
    }

    public async Task<int> ClearHistoryAsync(long? workspaceId, CancellationToken ct)
    {
        var deleted = await _runs.DeleteFinishedInAsync(workspaceId, ct).ConfigureAwait(false);
        if (deleted.Count == 0)
        {
            return 0;
        }

        foreach (var run in deleted)
        {
            RemoveStoredOutput(run);
        }

        var place = deleted[0].WorkspaceName;
        _logger.LogInformation("The owner cleared the command history of {Place}: {Count} run(s).", place, deleted.Count);
        await AuditHistoryAsync("command.history_clear", place, string.Create(CultureInfo.InvariantCulture, $"{deleted.Count} finished one-time run(s) deleted, #{deleted.Min(r => r.Id)} to #{deleted.Max(r => r.Id)}")).ConfigureAwait(false);
        await _events.PublishAsync(new CommandRunsDeleted(workspaceId, [.. deleted.Select(r => r.Id)])).ConfigureAwait(false);
        return deleted.Count;
    }

    private void RemoveStoredOutput(CommandRun run)
    {
        if (!string.IsNullOrWhiteSpace(run.OutputPath))
        {
            RunOutputFiles.Delete(run.OutputPath, _options.RunsDirectory, _options.TerminalLogFiles, _logger);
        }
    }

    // Deleting history is the owner's act in the admin UI, written down like approvals so Audit keeps a record of what went.
    private async Task AuditHistoryAsync(string action, string place, string details)
    {
        try
        {
            var entry = AuditEntries.Create(_time, AuditCategory.Approval, action, OwnerName, place, AuditOutcome.Ok, SensitiveDataScanner.Redact(details).Text, null, null);
            await _audit.AppendAsync(entry, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "'{Action}' could not be written to the audit log.", action);
        }
    }
    // ---- the run ----

    private async Task ExecuteAsync(ActiveRun run)
    {
        RunOutcome outcome;
        try
        {
            outcome = await RunAsync(run).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            outcome = new RunOutcome(CommandRunStatus.Cancelled, null, StopNote(run));
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
                return new RunOutcome(status, null, decision.Stopped ? StopNote(run) : decision.Reason ?? "Denied.");
            }

            await ApplyApprovalAsync(run, decision).ConfigureAwait(false);
        }

        // A command outside every workspace has no folder of the owner's to protect, so it does not wait for other system scripts.
        var workspaceLock = submission.Workspace is { } workspace && submission.Operation.Mutating
            ? _workspaceLocks.GetOrAdd(workspace.Id, _ => new SemaphoreSlim(1, 1))
            : null;
        using var locks = new LockHolder(workspaceLock, _global);
        if (workspaceLock is not null)
        {
            await AcquireAsync(workspaceLock, run).ConfigureAwait(false);
            locks.HoldsWorkspace = true;
        }

        await AcquireAsync(_global, run).ConfigureAwait(false);
        locks.HoldsGlobal = true;

        var drift = await FindDriftAsync(run).ConfigureAwait(false);
        if (drift is not null)
        {
            return new RunOutcome(CommandRunStatus.Failed, null, drift);
        }

        await SetStatusAsync(run, CommandRunStatus.Running, new CommandRunUpdate { StartedUtc = _time.GetUtcNow() }).ConfigureAwait(false);
        var result = await RunProgramAsync(run, locks).ConfigureAwait(false);
        if (result.Status == CommandRunStatus.Succeeded)
        {
            await RefreshBaselineAsync(run).ConfigureAwait(false);
        }

        return result;
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
                return new Decision(false, StopNote(run), null, Stopped: true);
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

        // A terminal command the owner approved may start again without asking while nothing it depends on changes. A shell script never does.
        if (run.Terminal is { } terminal)
        {
            var approval = run.Submission.Tier == CommandTier.Shell
                ? null
                : new TerminalApproval(
                    run.Submission.Operation.Key,
                    run.Submission.CommandLine,
                    run.Submission.WorkingDirectory,
                    run.Submission.Plan?.ExecutablePath,
                    BuildFingerprint.Serialize(run.Fingerprint ?? new Dictionary<string, string>()),
                    _time.GetUtcNow());
            await _terminals.UpdateAsync(terminal.Launch.TerminalId, new TerminalUpdate { Approval = approval, StateNote = string.Empty }, CancellationToken.None).ConfigureAwait(false);
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

    private async Task<RunOutcome> RunProgramAsync(ActiveRun run, LockHolder locks)
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
            var outcome = run.Terminal is not null
                ? await RunTerminalAsync(run, locks).ConfigureAwait(false)
                : submission.Tier == CommandTier.Shell
                    ? await RunShellAsync(run).ConfigureAwait(false)
                    : await RunRecipeAsync(run).ConfigureAwait(false);
            run.Elapsed = clock.Elapsed;
            run.Append(Footer(outcome, clock.Elapsed));
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
        run.Append($"\u001b[90m{submission.WorkingDirectory}\u001b[0m \u001b[32m$\u001b[0m {plan.CommandLine}\n");

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
                    ? new RunOutcome(CommandRunStatus.Cancelled, null, StopNote(run))
                    : new RunOutcome(CommandRunStatus.TimedOut, null, TimedOutNote(submission.Timeout));
            }
        }

        var program = LocateProgram(plan, out var missing);
        if (program is null)
        {
            return new RunOutcome(CommandRunStatus.Failed, null, missing);
        }

        var spec = new RunSpec(program, plan.Arguments, submission.WorkingDirectory, ChildEnvironment.Build(), submission.Timeout);
        return ToOutcome(await _runner.RunAsync(spec, run.Append, run.Stop.Token).ConfigureAwait(false), submission.Timeout, StopNote(run));
    }

    private async Task<RunOutcome> RunShellAsync(ActiveRun run)
    {
        var submission = run.Submission;
        var shell = submission.Shell ?? throw new InvalidOperationException("A shell run has no shell.");
        var script = run.Script ?? throw new InvalidOperationException("A shell run has no script.");
        run.Append($"\u001b[90m{submission.WorkingDirectory}\u001b[0m \u001b[32m$\u001b[0m \u001b[90m# {shell.DisplayName}\u001b[0m\n");
        run.Append(script.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n\u001b[90m# output\u001b[0m\n");

        using var file = await ShellScripts.WriteAsync(shell, script, run.Stop.Token).ConfigureAwait(false);
        var spec = new RunSpec(shell.FileName, ShellScripts.Arguments(shell, file.Path), submission.WorkingDirectory, ChildEnvironment.Build(), submission.Timeout);
        return ToOutcome(await _runner.RunAsync(spec, run.Append, run.Stop.Token).ConfigureAwait(false), submission.Timeout, StopNote(run));
    }

    // A program that keeps running in a terminal: the build before it (if any) runs to its end and holds the workspace's turn like any build; the
    // program then starts with its input open and holds the turn and a run slot only until it is up, it ended, or its start phase ran out.
    private async Task<RunOutcome> RunTerminalAsync(ActiveRun run, LockHolder locks)
    {
        var submission = run.Submission;
        var terminal = run.Terminal ?? throw new InvalidOperationException("A terminal run has no terminal.");
        try
        {
            if (terminal.Launch.Prepare is { } prepare)
            {
                run.Append($"\u001b[90m{submission.WorkingDirectory}\u001b[0m \u001b[32m$\u001b[0m {prepare.CommandLine}\n");
                var builder = LocateProgram(prepare, out var missingBuilder);
                if (builder is null)
                {
                    return new RunOutcome(CommandRunStatus.Failed, null, missingBuilder);
                }

                var buildSpec = new RunSpec(builder, prepare.Arguments, submission.WorkingDirectory, ChildEnvironment.Build(), prepare.Timeout);
                var built = ToOutcome(await _runner.RunAsync(buildSpec, run.Append, run.Stop.Token).ConfigureAwait(false), prepare.Timeout, StopNote(run));
                if (built.Status != CommandRunStatus.Succeeded)
                {
                    return built with { Note = built.Note ?? "The build before the run failed, so the app was not started." };
                }

                // The build is done; the app itself starts without building, so it no longer needs the workspace's turn.
                locks.ReleaseWorkspace();
            }

            run.Stop.Token.ThrowIfCancellationRequested();
            RunSpec spec;
            ScriptFile? scriptFile = null;
            if (submission.Tier == CommandTier.Shell)
            {
                var shell = submission.Shell ?? throw new InvalidOperationException("A shell run has no shell.");
                var script = run.Script ?? throw new InvalidOperationException("A shell run has no script.");
                run.Append($"\u001b[90m{submission.WorkingDirectory}\u001b[0m \u001b[32m$\u001b[0m \u001b[90m# {shell.DisplayName}, keeps running in terminal {terminal.Launch.Name}\u001b[0m\n");
                run.Append(script.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n\u001b[90m# output\u001b[0m\n");
                scriptFile = await ShellScripts.WriteAsync(shell, script, run.Stop.Token).ConfigureAwait(false);
                spec = new RunSpec(shell.FileName, ShellScripts.Arguments(shell, scriptFile.Path), submission.WorkingDirectory, ChildEnvironment.BuildForTerminal(), Timeout.InfiniteTimeSpan);
            }
            else
            {
                var plan = submission.Plan ?? throw new InvalidOperationException("A recipe run has no plan.");
                run.Append($"\u001b[90m{submission.WorkingDirectory}\u001b[0m \u001b[32m$\u001b[0m {plan.CommandLine}\n");
                var program = LocateProgram(plan, out var missing);
                if (program is null)
                {
                    return new RunOutcome(CommandRunStatus.Failed, null, missing);
                }

                spec = new RunSpec(program, plan.Arguments, submission.WorkingDirectory, ChildEnvironment.BuildForTerminal(), Timeout.InfiniteTimeSpan);
            }

            using (scriptFile)
            {
                return await RunTerminalProgramAsync(run, terminal, spec, locks).ConfigureAwait(false);
            }
        }
        finally
        {
            terminal.HandedOff.TrySetResult();
        }
    }

    private async Task<RunOutcome> RunTerminalProgramAsync(ActiveRun run, TerminalRuntime terminal, RunSpec spec, LockHolder locks)
    {
        var (process, failure) = _runner.Start(spec, run.AppendProgram, terminal.Launch.MaxLifetime);
        if (process is null)
        {
            return new RunOutcome(CommandRunStatus.Failed, null, failure);
        }

        using (process)
        {
            terminal.Process = process;

            // A stop asked for from now on ends the program the way a developer would; one asked for just before it started does so at once.
            using var registration = run.Stop.Token.Register(() => _ = StopProgramAsync(process));
            if (run.Stop.IsCancellationRequested)
            {
                _ = StopProgramAsync(process);
            }

            await _terminals.UpdateAsync(terminal.Launch.TerminalId, new TerminalUpdate
            {
                State = TerminalState.Starting,
                Pid = process.Pid,
                PidStartUtc = process.StartedUtc,
                Program = process.ProgramName,
                StateNote = string.Empty,
            }, CancellationToken.None).ConfigureAwait(false);
            await PublishTerminalAsync(terminal.Launch.TerminalId, run.Submission, TerminalState.Starting).ConfigureAwait(false);

            using var loopStop = new CancellationTokenSource();
            var loop = TerminalLoopAsync(run, terminal, process, loopStop.Token);
            try
            {
                await Task.WhenAny(terminal.Ready.Task, process.Exited, Task.Delay(_options.TerminalStartLock)).ConfigureAwait(false);
                HandOff(run, terminal, locks);
                var exitCode = await process.Exited.ConfigureAwait(false);
                if (run.StopActor is not null || process.IsStopping && !process.LifetimeEnded)
                {
                    return new RunOutcome(CommandRunStatus.Cancelled, exitCode, StopNote(run));
                }

                if (process.LifetimeEnded)
                {
                    var minutes = terminal.Launch.MaxLifetime?.TotalMinutes ?? 0;
                    return new RunOutcome(CommandRunStatus.TimedOut, exitCode, string.Create(CultureInfo.InvariantCulture, $"Stopped after the most it may run, {minutes:0.#} minutes."));
                }

                return exitCode == 0
                    ? new RunOutcome(CommandRunStatus.Succeeded, 0, "The program ended by itself.")
                    : new RunOutcome(CommandRunStatus.Failed, exitCode, "The program ended by itself with an error.");
            }
            finally
            {
                await loopStop.CancelAsync().ConfigureAwait(false);
                await loop.ConfigureAwait(false);
            }
        }
    }

    private async Task StopProgramAsync(RunningProcess process)
    {
        try
        {
            await process.StopAsync(_tree, _options.GracefulStop).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "A terminal program could not be stopped cleanly; it is killed.");
            process.Kill();
        }
    }

    // The program is up, it ended, or its start phase ran out: it gives back the workspace's turn and its run slot, and leaves the "happening now" list.
    private static void HandOff(ActiveRun run, TerminalRuntime terminal, LockHolder locks)
    {
        locks.ReleaseAll();
        if (!terminal.HandedOff.TrySetResult())
        {
            return;
        }

        run.Preview?.Complete();
        run.LiveItem?.Dispose();
        run.LiveItem = null;
    }

    // Picks up what the reading thread found (up, addresses, the app going down or coming back, new errors, a prompt) and watches the ports the
    // program's processes listen on. Store writes and events happen here, never on the reading thread.
    private async Task TerminalLoopAsync(ActiveRun run, TerminalRuntime terminal, RunningProcess process, CancellationToken stop)
    {
        var nextPortPoll = DateTimeOffset.UtcNow;
        var noReadyNoted = false;
        var started = DateTimeOffset.UtcNow;
        var noPatterns = terminal.Launch.ReadyPatterns.Count == 0 && terminal.Launch.ReadyPort is null;
        using var timer = new PeriodicTimer(TerminalTick);
        try
        {
            while (await timer.WaitForNextTickAsync(stop).ConfigureAwait(false))
            {
                try
                {
                    var now = DateTimeOffset.UtcNow;
                    if (now >= nextPortPoll && !process.HasExited)
                    {
                        nextPortPoll = now + (terminal.Detector.IsReady ? PortPollRunning : PortPollStarting);
                        await PollPortsAsync(terminal, process, stop).ConfigureAwait(false);
                    }

                    // A script with nothing to wait for counts as up once it went quiet after its first output (its own output, not Waymark's lines).
                    if (!terminal.Detector.IsReady && terminal.Launch.IsShell && noPatterns
                        && terminal.FirstOutputUtc is not null && terminal.LastProgramOutputUtc is { } last && now - last >= ShellReadyQuiet)
                    {
                        terminal.Detector.MarkReady("went quiet after its first output (assumed)");
                    }

                    await ApplyTerminalChangesAsync(run, terminal, now).ConfigureAwait(false);

                    if (!terminal.Detector.IsReady && !noReadyNoted && now - started >= terminal.Launch.ReadyTimeout)
                    {
                        noReadyNoted = true;
                        await _terminals.UpdateAsync(terminal.Launch.TerminalId, new TerminalUpdate
                        {
                            StateNote = string.Create(CultureInfo.InvariantCulture, $"No sign that it is up after {terminal.Launch.ReadyTimeout.TotalSeconds:0} s; it keeps running."),
                        }, CancellationToken.None).ConfigureAwait(false);
                        await PublishTerminalAsync(terminal.Launch.TerminalId, run.Submission, TerminalState.Starting).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Watching terminal {Terminal} failed once; it goes on.", terminal.Launch.Name);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The program ended.
        }

        // What the last output said (an error, the address) is kept before the run is finished.
        try
        {
            await ApplyTerminalChangesAsync(run, terminal, DateTimeOffset.UtcNow).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The last state of terminal {Terminal} could not be saved.", terminal.Launch.Name);
        }
    }

    private async Task PollPortsAsync(TerminalRuntime terminal, RunningProcess process, CancellationToken ct)
    {
        var below = await _tree.DescendantsAsync(process.Pid, ct).ConfigureAwait(false);
        process.Remember(below);
        var ours = new HashSet<int>(below) { process.Pid };
        IReadOnlyList<PortOwner> owners;
        try
        {
            owners = await _ports.ListAsync(ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            return;
        }

        terminal.Detector.CheckPorts(owners.Where(owner => ours.Contains(owner.Pid)).Select(owner => owner.Port).Distinct());
    }

    private async Task ApplyTerminalChangesAsync(ActiveRun run, TerminalRuntime terminal, DateTimeOffset now)
    {
        var id = terminal.Launch.TerminalId;
        var name = terminal.Launch.Name;
        var workspaceId = run.Submission.Workspace?.Id;
        terminal.Log.Flush();

        if (terminal.Detector.IsReady && terminal.Ready.TrySetResult())
        {
            var ready = terminal.Health.AppDown ? TerminalState.AppDown : TerminalState.Ready;
            await _terminals.UpdateAsync(id, new TerminalUpdate
            {
                State = ready,
                ReadyHow = terminal.Detector.How,
                ReadyUtc = _time.GetUtcNow(),
                Urls = terminal.Detector.Urls,
                Ports = terminal.Detector.Ports,
                StateNote = string.Empty,
            }, CancellationToken.None).ConfigureAwait(false);
            terminal.Detector.AddressesChanged = false;
            terminal.Health.AppStateChanged = false;
            _logger.LogCommand(LogLevel.Information, run.Id, "ready", run.Submission.WorkspaceName, $"terminal {name} is up: {terminal.Detector.How}", run.Submission.Purpose);
            await PublishTerminalAsync(id, run.Submission, ready).ConfigureAwait(false);
        }

        if (terminal.Detector.AddressesChanged)
        {
            terminal.Detector.AddressesChanged = false;
            await _terminals.UpdateAsync(id, new TerminalUpdate { Urls = terminal.Detector.Urls, Ports = terminal.Detector.Ports }, CancellationToken.None).ConfigureAwait(false);
            await PublishTerminalAsync(id, run.Submission, CurrentState(terminal)).ConfigureAwait(false);
        }

        if (terminal.Health.AppStateChanged && terminal.Detector.IsReady)
        {
            terminal.Health.AppStateChanged = false;
            var state = CurrentState(terminal);
            await _terminals.UpdateAsync(id, new TerminalUpdate
            {
                State = state,
                StateNote = state == TerminalState.AppDown ? "The program runs, but the app inside it stopped; it waits for a change." : string.Empty,
            }, CancellationToken.None).ConfigureAwait(false);
            await PublishTerminalAsync(id, run.Submission, state).ConfigureAwait(false);
            if (state == TerminalState.AppDown && run.StopActor is null)
            {
                await NoticeAsync(id, workspaceId, name, TerminalNoticeKind.AppDown, $"terminal '{name}': the app stopped inside its watcher (it waits for a file change).").ConfigureAwait(false);
            }
        }

        // New error lines Claude has neither read nor been told about. Nothing is announced while the program is being stopped: what it prints
        // on its way out is not news, and the terminal may be gone by the time Claude hears of it.
        var seen = Math.Max(terminal.NotifiedErrorLine, terminal.ReadLine);
        if (run.StopActor is null && terminal.Health.LastErrorLine > seen && now - terminal.NotifiedErrorUtc >= ErrorNoticeGap)
        {
            var count = terminal.Health.ErrorsAfter(seen);
            var first = terminal.Health.ErrorLinesAfter(seen, 1).FirstOrDefault();
            terminal.NotifiedErrorLine = terminal.Health.LastErrorLine;
            terminal.NotifiedErrorUtc = now;
            var sample = first.Text is { Length: > 0 } text ? ": " + Cut(text, 140) : string.Empty;
            await NoticeAsync(id, workspaceId, name, TerminalNoticeKind.NewErrors, string.Create(CultureInfo.InvariantCulture, $"terminal '{name}' printed {count} new error line(s){sample}")).ConfigureAwait(false);
        }

        // A prompt: the last text has no line break and nothing came for a moment.
        var pending = terminal.Log.PendingText.Trim();
        terminal.PendingPrompt = pending.Length > 0 && terminal.Log.LastTextUtc is { } lastText && now - lastText >= PromptQuiet ? Cut(pending, 200) : null;
        if (run.StopActor is null && terminal.PendingPrompt is { } prompt && !string.Equals(prompt, terminal.NotifiedPrompt, StringComparison.Ordinal))
        {
            terminal.NotifiedPrompt = prompt;
            await NoticeAsync(id, workspaceId, name, TerminalNoticeKind.WaitsForInput, $"terminal '{name}' seems to wait for an answer: \"{prompt}\"").ConfigureAwait(false);
        }

        if (terminal.Log.LastTextUtc is { } output && output - terminal.SavedOutputUtc >= OutputSaveGap)
        {
            terminal.SavedOutputUtc = output;
            await _terminals.UpdateAsync(id, new TerminalUpdate { LastOutputUtc = output }, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static TerminalState CurrentState(TerminalRuntime terminal) =>
        !terminal.Detector.IsReady ? TerminalState.Starting : terminal.Health.AppDown ? TerminalState.AppDown : TerminalState.Ready;

    private string? LocateProgram(CommandPlan plan, out string missing)
    {
        var program = plan.ExecutablePath is { } explicitPath ? (File.Exists(explicitPath) ? explicitPath : null) : _executables.Find(plan.Program);
        missing = plan.ExecutablePath is null
            ? $"'{plan.Program}' was not found in the folders of PATH on this computer."
            : $"'{plan.Program}' does not exist any more: {plan.ExecutablePath}";
        return program;
    }

    private static RunOutcome ToOutcome(RunResult result, TimeSpan timeout, string stopNote)
    {
        if (result.StartFailure is not null)
        {
            return new RunOutcome(CommandRunStatus.Failed, null, result.StartFailure);
        }

        if (result.Cancelled)
        {
            return new RunOutcome(CommandRunStatus.Cancelled, result.ExitCode, stopNote);
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

    private static string StopNote(ActiveRun run) => run.StopNote ?? run.StopActor switch
    {
        TerminalActors.Claude => "Stopped by Claude.",
        TerminalActors.Waymark => "Stopped by Waymark.",
        _ => "Stopped by the owner.",
    };

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
            _logger.LogCommand(LogLevel.Information, run.Id, "started", run.Submission.WorkspaceName, string.Empty, run.Submission.Purpose);
        }

        await _events.PublishAsync(new CommandRunChanged(run.Id, run.Submission.Workspace?.Id, status)).ConfigureAwait(false);
    }

    private async Task FinishAsync(ActiveRun run, RunOutcome outcome)
    {
        try
        {
            run.Preview?.Complete();
            _logger.LogCommand(LevelOf(outcome.Status), run.Id, StageOf(outcome.Status), run.Submission.WorkspaceName, FinishDetail(run, outcome), run.Submission.Purpose);
            await PublishPendingAsync(run).ConfigureAwait(false);
            string? path = null;
            long bytes;
            var note = outcome.Note;
            if (run.Terminal is { } terminal)
            {
                // The output of a terminal run was written to disk as it came; the memory copy may lack its middle, so it must not replace the file.
                terminal.Log.Dispose();
                bytes = terminal.Log.Bytes;
                path = bytes > 0 && File.Exists(terminal.Log.LogPath) ? terminal.Log.LogPath : null;
            }
            else
            {
                var (text, _) = run.Output.Snapshot();
                bytes = Encoding.UTF8.GetByteCount(text);
                if (text.Length > 0)
                {
                    path = LogPathOf(run.Id);
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
            }

            var update = new CommandRunUpdate
            {
                Status = outcome.Status,
                ExitCode = outcome.ExitCode,
                EndedUtc = _time.GetUtcNow(),
                Note = note,
                OutputBytes = bytes,
                OutputPath = path,
                StoppedBy = outcome.Status == CommandRunStatus.Cancelled ? run.StopActor : null,
            };
            await _runs.UpdateAsync(run.Id, update, CancellationToken.None).ConfigureAwait(false);
            run.Status = outcome.Status;
            if (run.Terminal is not null)
            {
                await FinishTerminalAsync(run, outcome).ConfigureAwait(false);
            }

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
            run.Terminal?.Ready.TrySetResult();
            run.Terminal?.HandedOff.TrySetResult();
            run.Done.TrySetResult();
        }
    }

    // The terminal keeps the end of its program: who stopped it, or that it ended by itself, and Claude hears of a crash on its next call.
    private async Task FinishTerminalAsync(ActiveRun run, RunOutcome outcome)
    {
        var terminal = run.Terminal!;
        var id = terminal.Launch.TerminalId;
        var name = terminal.Launch.Name;
        var started = terminal.Process is not null;
        var state = outcome.Status switch
        {
            CommandRunStatus.Denied => TerminalState.Idle,
            CommandRunStatus.Cancelled when !started => TerminalState.Idle,
            CommandRunStatus.Cancelled when run.StopActor == TerminalActors.Waymark => TerminalState.WaymarkStopped,
            CommandRunStatus.Cancelled => TerminalState.Stopped,
            CommandRunStatus.TimedOut => TerminalState.Stopped,
            CommandRunStatus.Succeeded => TerminalState.Exited,
            _ => TerminalState.Crashed,
        };
        var note = outcome.Status switch
        {
            CommandRunStatus.Denied => "Not started: " + (outcome.Note ?? "denied."),
            _ => outcome.Note ?? string.Empty,
        };
        await _terminals.UpdateAsync(id, new TerminalUpdate
        {
            State = state,
            ClearCurrentRun = true,
            LastRunId = run.Id,
            ExitCode = outcome.ExitCode,
            EndedUtc = _time.GetUtcNow(),
            StoppedBy = outcome.Status == CommandRunStatus.Cancelled ? run.StopActor : null,
            StateNote = note,
            Urls = terminal.Detector.Urls,
            Ports = terminal.Detector.Ports,
        }, CancellationToken.None).ConfigureAwait(false);
        await PublishTerminalAsync(id, run.Submission, state).ConfigureAwait(false);

        var workspaceId = run.Submission.Workspace?.Id;
        var code = outcome.ExitCode is { } exit ? string.Create(CultureInfo.InvariantCulture, $" (exit {exit})") : string.Empty;
        switch (state)
        {
            case TerminalState.Crashed:
                await NoticeAsync(id, workspaceId, name, TerminalNoticeKind.Crashed, $"terminal '{name}' crashed{code}{(started ? string.Empty : " before its program started")}: {Cut(note, 160)}").ConfigureAwait(false);
                break;
            case TerminalState.Exited:
                await NoticeAsync(id, workspaceId, name, TerminalNoticeKind.Exited, $"terminal '{name}' ended by itself{code}.").ConfigureAwait(false);
                break;
            case TerminalState.Stopped when run.StopActor == TerminalActors.Owner:
                await NoticeAsync(id, workspaceId, name, TerminalNoticeKind.StoppedByOwner, $"the owner stopped terminal '{name}'. Do not start it again unless the user asks.").ConfigureAwait(false);
                break;
        }
    }

    private async Task PublishTerminalAsync(long terminalId, CommandSubmission submission, TerminalState state) =>
        await _events.PublishAsync(new TerminalChanged(terminalId, submission.Workspace?.Id, state)).ConfigureAwait(false);

    private async Task NoticeAsync(long terminalId, long? workspaceId, string name, TerminalNoticeKind kind, string text)
    {
        var seq = Interlocked.Increment(ref _noticeSeq);
        var clean = SensitiveDataScanner.Redact(text).Text;
        await _events.PublishAsync(new TerminalNotice(terminalId, workspaceId, name, kind, clean, seq, _time.GetUtcNow())).ConfigureAwait(false);
    }

    private bool ReusesApproval(TerminalApproval prior, CommandSubmission submission, PolicyOutcome outcome, DateTimeOffset now)
    {
        if (submission.Tier == CommandTier.Shell || submission.Operation.Key == CommandCatalog.ShellRun || now - prior.ApprovedUtc > _options.TerminalApprovalAge)
        {
            return false;
        }

        if (!string.Equals(prior.Operation, submission.Operation.Key, StringComparison.Ordinal)
            || !string.Equals(prior.CommandLine, submission.CommandLine, StringComparison.Ordinal)
            || !string.Equals(prior.WorkingDirectory, submission.WorkingDirectory, StringComparison.Ordinal)
            || !string.Equals(prior.ExecutablePath, submission.Plan?.ExecutablePath, StringComparison.Ordinal))
        {
            return false;
        }

        var approved = BuildFingerprint.Deserialize(prior.FingerprintJson);
        var current = outcome.Fingerprint ?? new Dictionary<string, string>();
        return BuildFingerprint.Differences(approved, current).Count == 0;
    }

    private string LogPathOf(long runId) => Path.Combine(_options.RunsDirectory, string.Create(CultureInfo.InvariantCulture, $"run-{runId}.log"));

    private static async Task<string> ReadSharedAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
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
                reason,
                run.Submission.Purpose);
            await _audit.AppendAsync(entry, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The decision on command run {RunId} could not be written to the audit log.", run.Id);
        }
    }

    private async Task AuditAsync(AuditCategory category, string action, string actor, CommandSubmission submission, string details)
    {
        try
        {
            var entry = AuditEntries.Create(
                _time,
                category,
                action,
                actor == TerminalActors.Owner ? OwnerName : actor,
                submission.WorkspaceName,
                AuditOutcome.Ok,
                $"{submission.Operation.Key}: {SensitiveDataScanner.Redact(details).Text}",
                null,
                submission.Purpose);
            await _audit.AppendAsync(entry, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "'{Action}' could not be written to the audit log.", action);
        }
    }

    private static string Cut(string text, int max = MaxAuditChars)
    {
        var line = text.ReplaceLineEndings(" ");
        return line.Length > max ? line[..max] + "..." : line;
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
        var text = submission.Tier == CommandTier.Shell ? "shell: " + shown : shown;
        return submission.Terminal is { } terminal ? $"[terminal {terminal.Name}] {text}" : text;
    }

    private static string ActivityLabel(long id, CommandSubmission submission, CommandRunStatus status)
    {
        var label = string.Create(CultureInfo.InvariantCulture, $"#{id} {submission.Operation.Label} in {submission.WorkspaceName}: {StageOf(status)}");
        return string.IsNullOrWhiteSpace(submission.Purpose) ? label : label + ActivityLogging.WhyMarker + submission.Purpose;
    }

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
            now,
            s.Terminal?.TerminalId);

    private sealed record RunOutcome(CommandRunStatus Status, int? ExitCode, string? Note);

    private sealed record Decision(bool Approve, string? Reason, string? EditedScript, bool Stopped = false);

    // The workspace's turn and the run slot one run holds. Releasing twice is harmless, so a terminal run can give them back while it keeps running.
    private sealed class LockHolder(SemaphoreSlim? workspace, SemaphoreSlim global) : IDisposable
    {
        private readonly object _gate = new();

        public bool HoldsWorkspace { get; set; }

        public bool HoldsGlobal { get; set; }

        public void ReleaseWorkspace()
        {
            lock (_gate)
            {
                if (HoldsWorkspace)
                {
                    HoldsWorkspace = false;
                    workspace?.Release();
                }
            }
        }

        public void ReleaseAll()
        {
            lock (_gate)
            {
                if (HoldsGlobal)
                {
                    HoldsGlobal = false;
                    global.Release();
                }
            }

            ReleaseWorkspace();
        }

        public void Dispose() => ReleaseAll();
    }

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

        /// <summary>The live side of a run in a terminal; null for a run that runs to its end.</summary>
        public TerminalRuntime? Terminal { get; set; }

        /// <summary>Who asked to stop the run (claude, owner, waymark); null while nobody did.</summary>
        public string? StopActor { get; set; }

        /// <summary>A note that replaces the usual "Stopped by ..." (the workspace was switched off, Waymark stops).</summary>
        public string? StopNote { get; set; }

        /// <summary>The line of this run in the Live page's "happening now" list. Ends with the run, or when a terminal program is up.</summary>
        public ActivityScope? LiveItem { get; set; }

        /// <summary>Picks the first lines of the program's output for the Live page.</summary>
        public OutputPreview? Preview { get; set; }

        /// <summary>How long the program ran; zero when it never started.</summary>
        public TimeSpan Elapsed { get; set; }

        /// <summary>Takes text Waymark writes for the run (the command line, a script, the build before a terminal run, markers) and, for a run that
        /// ends by itself, the program's output: to the run's output, the first lines to the Live page, and in a terminal to its log only.</summary>
        public void Append(string text)
        {
            Output.Append(text);
            Preview?.Add(text);
            Terminal?.OnText(text, fromProgram: false);
        }

        /// <summary>Takes what a terminal's program printed: as Append, and in the terminal also to its readiness and health checks.</summary>
        public void AppendProgram(string text)
        {
            Output.Append(text);
            Preview?.Add(text);
            Terminal?.OnText(text, fromProgram: true);
        }

        public CancellationTokenSource Stop { get; } = new();

        public TaskCompletionSource<Decision> Decision { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
