using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Waymark.Core.Commands;
using Waymark.Core.Events;
using Waymark.Core.Workspaces;
using Waymark.Modules.Commands.Execution;

namespace Waymark.Modules.Commands.Terminals;

/// <summary>
/// What <see cref="TerminalCoordinator.PrepareAsync"/> found: the terminal to start in, and, when the same command already runs there, that run.
/// Created is true when this call made the terminal, so a start that is refused after it can remove it again.
/// </summary>
public sealed record TerminalPreparation(Terminal Terminal, CommandRun? Running, bool IsDuplicate, bool Created = false);

/// <summary>
/// The terminals of every workspace and of the computer: named slots that run one program at a time and keep it running until it is stopped. It
/// finds or creates a terminal for a command, refuses a second program in a busy one (unless Claude asks to replace it, which is Ctrl+C then the
/// new command), and keeps to the most programs that may run at once. For the admin UI it shows the terminals and their output, and stops one:
/// that is all the owner does here. It stops the terminals of a workspace that is switched off or removed.
/// </summary>
public sealed class TerminalCoordinator : ITerminals, IDisposable
{
    private const int EarlierRuns = 3;
    private const int EarlierTailLines = 60;

    // How long a replace or a close waits for the old program: Ctrl+C and terminate each get the grace time, then the output is drained.
    private static readonly TimeSpan DrainAllowance = TimeSpan.FromSeconds(20);

    // One start at a time, from the checks (busy, the most at once) until the run is registered, so two starts cannot both pass them.
    private readonly SemaphoreSlim _startGate = new(1, 1);

    private readonly ITerminalStore _store;
    private readonly ICommandRunStore _runs;
    private readonly CommandCoordinator _commands;
    private readonly IEventBus _events;
    private readonly CommandsOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<TerminalCoordinator> _logger;
    private readonly IDisposable _workspaceSubscription;

    public TerminalCoordinator(
        ITerminalStore store,
        ICommandRunStore runs,
        CommandCoordinator commands,
        IEventBus events,
        CommandsOptions options,
        TimeProvider time,
        ILogger<TerminalCoordinator> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _runs = runs ?? throw new ArgumentNullException(nameof(runs));
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _workspaceSubscription = events.Subscribe<WorkspaceChanged>(OnWorkspaceChangedAsync);
    }

    public ITerminalStore Store => _store;

    // ---- ITerminals: what the admin UI and the guides see ----

    public async Task<IReadOnlyList<TerminalInfo>> ListAsync(TerminalQuery query, CancellationToken ct)
    {
        var terminals = await _store.ListAsync(query, ct).ConfigureAwait(false);
        return terminals.Select(Describe).ToList();
    }

    public async Task<TerminalInfo?> GetAsync(long id, CancellationToken ct)
    {
        var terminal = await _store.GetAsync(id, ct).ConfigureAwait(false);
        return terminal is null ? null : Describe(terminal);
    }

    public async Task<TerminalOutput?> GetOutputAsync(long id, CancellationToken ct)
    {
        var terminal = await _store.GetAsync(id, ct).ConfigureAwait(false);
        if (terminal is null)
        {
            return null;
        }

        var runs = await _runs.ListByTerminalAsync(id, EarlierRuns + 1, ct).ConfigureAwait(false);
        var currentId = terminal.CurrentRunId is { } current && _commands.IsActive(current) ? current : (long?)null;
        var shownId = currentId ?? runs.FirstOrDefault()?.Id;
        var text = new StringBuilder();
        foreach (var earlier in runs.Where(r => r.Id != shownId).Take(EarlierRuns).Reverse())
        {
            text.Append(Separator(earlier));
            text.Append(await TailAsync(earlier, EarlierTailLines, ct).ConfigureAwait(false));
        }

        if (shownId is not { } runId)
        {
            return new TerminalOutput(text.ToString(), null, 0, false);
        }

        var shown = runs.FirstOrDefault(r => r.Id == runId) ?? await _runs.GetAsync(runId, ct).ConfigureAwait(false);
        if (shown is not null)
        {
            text.Append(Separator(shown));
        }

        var snapshot = await _commands.GetOutputAsync(runId, ct).ConfigureAwait(false);
        text.Append(snapshot?.Text ?? string.Empty);
        return new TerminalOutput(text.ToString(), runId, snapshot?.Seq ?? 0, snapshot?.IsLive ?? false);
    }

    public Task<IReadOnlyList<CommandRun>> ListRunsAsync(long id, int limit, CancellationToken ct) => _runs.ListByTerminalAsync(id, limit, ct);

    public async Task<bool> StopAsync(long id, string actor, CancellationToken ct)
    {
        var terminal = await _store.GetAsync(id, ct).ConfigureAwait(false);
        return terminal?.CurrentRunId is { } runId && _commands.IsActive(runId) && await _commands.StopAsync(runId, actor, ct).ConfigureAwait(false);
    }

    public Task<int> StopAllAsync(string actor, CancellationToken ct) => _commands.StopTerminalsAsync(null, allScopes: true, actor, null, ct);

    /// <summary>
    /// Held by a start from <see cref="PrepareAsync"/> until its run is submitted (the run is then active and counts for the next start's checks).
    /// Dispose the result to let the next start in.
    /// </summary>
    public async Task<IDisposable> EnterStartAsync(CancellationToken ct)
    {
        await _startGate.WaitAsync(ct).ConfigureAwait(false);
        return new GateRelease(_startGate);
    }

    // ---- for Claude's tools (through CommandService) ----

    /// <summary>The live side of the terminal's program, when one runs now.</summary>
    public TerminalRuntime? RuntimeOf(Terminal terminal)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        return terminal.CurrentRunId is { } runId ? _commands.GetTerminalRuntime(runId) : null;
    }

    /// <summary>True when a program runs in the terminal now.</summary>
    public bool IsRunning(Terminal terminal)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        return terminal.CurrentRunId is { } runId && _commands.IsActive(runId);
    }

    /// <summary>
    /// Finds or creates the terminal of a workspace (null: of the computer) for a new command. When the same command already runs there it is
    /// returned as a duplicate; another command is refused unless replace, which stops it first. The most programs at once is checked before a
    /// terminal is created, so a refused start leaves no terminal behind. Call it inside <see cref="EnterStartAsync"/>.
    /// </summary>
    public async Task<TerminalPreparation> PrepareAsync(
        Workspace? workspace,
        string scopeName,
        string name,
        string folder,
        string operation,
        string commandLine,
        bool replace,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var terminal = await _store.GetByNameAsync(workspace?.Id, name, ct).ConfigureAwait(false);
        if (terminal is null)
        {
            await EnsureRoomAsync(null, ct).ConfigureAwait(false);
            var created = await CreateAsync(workspace, scopeName, name, folder, ct).ConfigureAwait(false);
            return new TerminalPreparation(created, null, IsDuplicate: false, Created: true);
        }

        if (terminal.CurrentRunId is { } runId && _commands.IsActive(runId))
        {
            var running = await _runs.GetAsync(runId, ct).ConfigureAwait(false);
            var same = running is not null
                && string.Equals(running.Operation, operation, StringComparison.Ordinal)
                && string.Equals(running.CommandLine, commandLine, StringComparison.Ordinal)
                && string.Equals(running.WorkingDirectory, folder, StringComparison.Ordinal);
            if (!replace)
            {
                if (same)
                {
                    return new TerminalPreparation(terminal, running, IsDuplicate: true);
                }

                throw new ArgumentException(
                    $"Terminal '{name}' already runs `{Short(running?.CommandLine ?? terminal.CommandLine ?? "a program")}` (run #{runId}). " +
                    "Use another terminal name, stop it first (cmd_terminal action stop), or pass replace: true to stop it and start this command instead.");
            }

            await StopAndWaitAsync(runId, TerminalActors.Claude, ct).ConfigureAwait(false);
            if (_commands.IsActive(runId))
            {
                throw new ArgumentException(
                    string.Create(CultureInfo.InvariantCulture, $"Terminal '{name}' did not stop within {StopWait.TotalSeconds:0} s, so nothing new was started in it. ") +
                    "Read it (cmd_terminal action read) to see why, then try again.");
            }

            terminal = await _store.GetAsync(terminal.Id, ct).ConfigureAwait(false) ?? terminal;
        }

        await EnsureRoomAsync(terminal.Id, ct).ConfigureAwait(false);
        return new TerminalPreparation(terminal, null, IsDuplicate: false);
    }

    /// <summary>How long a replace or close waits for the old program to end.</summary>
    public TimeSpan StopWait => _options.GracefulStop + _options.GracefulStop + DrainAllowance;

    // Refuses when as many programs as the owner allows run already, the one in the terminal being started (if any) not counted.
    private async Task EnsureRoomAsync(long? startingIn, CancellationToken ct)
    {
        var live = await _store.ListAsync(new TerminalQuery(LiveOnly: true), ct).ConfigureAwait(false);
        var running = live.Where(t => t.Id != startingIn && t.CurrentRunId is { } id && _commands.IsActive(id)).ToList();
        if (running.Count >= _options.MaxLiveTerminals)
        {
            var names = string.Join(", ", running.Select(t => $"{t.Scope}/{t.Name}"));
            throw new ArgumentException(
                $"{running.Count} terminals already run a program, the most the owner allows ({_options.MaxLiveTerminals}): {names}. Stop one you no longer need first.");
        }
    }

    private async Task StopAndWaitAsync(long runId, string actor, CancellationToken ct)
    {
        await _commands.StopAsync(runId, actor, ct).ConfigureAwait(false);
        await _commands.WaitEndedAsync([runId], StopWait).ConfigureAwait(false);
    }

    /// <summary>Stops what runs in the terminal (if anything), waits for it to end, and removes the terminal. Its runs stay in the history.</summary>
    /// <summary>Stops what runs in the terminal (if anything), waits for it to end, and removes the terminal. Its runs stay in the history.</summary>
    public async Task CloseAsync(Terminal terminal, string actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        if (terminal.CurrentRunId is { } runId && _commands.IsActive(runId))
        {
            await StopAndWaitAsync(runId, actor, ct).ConfigureAwait(false);
        }

        await _store.DeleteAsync(terminal.Id, ct).ConfigureAwait(false);
        await _events.PublishAsync(new TerminalChanged(terminal.Id, terminal.WorkspaceId, TerminalState.Idle, Closed: true)).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _workspaceSubscription.Dispose();
        _startGate.Dispose();
    }

    private TerminalInfo Describe(Terminal terminal)
    {
        var runtime = RuntimeOf(terminal);
        if (runtime is null)
        {
            return new TerminalInfo(terminal, 0, null, 0, null, 0);
        }

        return new TerminalInfo(
            terminal,
            runtime.Health.ErrorsAfter(runtime.ReadLine),
            runtime.PendingPrompt,
            runtime.ReadLine,
            runtime.ReadUtc,
            runtime.Log.LineCount);
    }

    private async Task<Terminal> CreateAsync(Workspace? workspace, string scopeName, string name, string folder, CancellationToken ct)
    {
        try
        {
            var created = await _store.CreateAsync(new NewTerminal(workspace?.Id, scopeName, name, folder, _time.GetUtcNow()), ct).ConfigureAwait(false);
            await _events.PublishAsync(new TerminalChanged(created.Id, created.WorkspaceId, created.State)).ConfigureAwait(false);
            return created;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Another call created the same terminal a moment ago (the name is unique per workspace).
            return await _store.GetByNameAsync(workspace?.Id, name, ct).ConfigureAwait(false) ?? throw new InvalidOperationException($"Terminal '{name}' could not be created: {ex.Message}", ex);
        }
    }

    private async ValueTask OnWorkspaceChangedAsync(WorkspaceChanged change)
    {
        if (change.Kind is not (WorkspaceChangeKind.Disabled or WorkspaceChangeKind.Removed))
        {
            return;
        }

        try
        {
            var note = change.Kind == WorkspaceChangeKind.Removed
                ? "Stopped: the workspace was removed from Waymark."
                : "Stopped: the workspace was switched off.";
            var stopped = await _commands.StopTerminalsAsync(change.WorkspaceId, allScopes: false, TerminalActors.Waymark, note, CancellationToken.None).ConfigureAwait(false);
            if (stopped > 0)
            {
                _logger.LogInformation("Stopped {Count} terminal program(s) of workspace {Workspace}: it was {Change}.", stopped, change.Name, change.Kind);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The terminals of workspace {Workspace} could not be stopped after it was {Change}.", change.Name, change.Kind);
        }
    }

    private static string Separator(CommandRun run)
    {
        var when = (run.StartedUtc ?? run.RequestedUtc).ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
        var how = run.Status switch
        {
            CommandRunStatus.Cancelled => run.StoppedBy switch
            {
                TerminalActors.Claude => " · stopped by Claude",
                TerminalActors.Waymark => " · ended by Waymark",
                _ => " · stopped by you",
            },
            CommandRunStatus.Failed => run.ExitCode is { } code ? string.Create(CultureInfo.InvariantCulture, $" · crashed, exit {code}") : " · failed",
            CommandRunStatus.Succeeded => " · ended by itself",
            CommandRunStatus.Denied => " · not started",
            _ => string.Empty,
        };
        return string.Create(CultureInfo.InvariantCulture, $"\u001b[90m── #{run.Id} · {Short(run.CommandLine)} · {when}{how} ──\u001b[0m\n");
    }

    private static async Task<string> TailAsync(CommandRun run, int lines, CancellationToken ct)
    {
        if (run.OutputPath is null || !File.Exists(run.OutputPath))
        {
            return string.Empty;
        }

        try
        {
            await using var stream = new FileStream(run.OutputPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            const int MaxTailBytes = 64 * 1024;
            if (stream.Length > MaxTailBytes)
            {
                stream.Seek(-MaxTailBytes, SeekOrigin.End);
            }

            using var reader = new StreamReader(stream, Encoding.UTF8);
            var text = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            var all = text.Split('\n');
            var kept = all.Skip(Math.Max(0, all.Length - lines - 1));
            return string.Join('\n', kept).TrimEnd() + "\n";
        }
        catch (IOException)
        {
            return string.Empty;
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

    private sealed class GateRelease(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                gate.Release();
            }
        }
    }
}
