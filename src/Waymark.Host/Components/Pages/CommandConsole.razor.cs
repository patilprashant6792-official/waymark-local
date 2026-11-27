using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;
using Waymark.Core.Commands;
using Waymark.Core.Events;
using Waymark.Core.Workspaces;
using Waymark.Host.Components.Shared;
using Waymark.Host.Terminal;
using Waymark.Host.Ui;
using Waymark.Host.Components.Ui;
using Waymark.Modules.Commands.Execution;

namespace Waymark.Host.Components.Pages;

public partial class CommandConsole : LiveComponentBase
{
    private IReadOnlyList<WmTab> TabList =>
        [.. Tabs.Select(t => new WmTab(t.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), t.Name, t.Active + t.Live, $"{t.Active} command(s) running or waiting, {t.Live} terminal(s) running"))];

    private string SelectedTab => _tab.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private Task OnTabChangedAsync(string id) => SelectTabAsync(long.Parse(id, System.Globalization.CultureInfo.InvariantCulture));
    private const long SystemTab = -1;
    private const int ListLimit = 200;
    private const int TabLimit = 60;
    private const int ShortChars = 90;
    private const string TerminalId = "wm-term";

    private readonly List<CommandOutputChunk> _early = [];
    private IReadOnlyList<Workspace> _workspaces = [];
    private IReadOnlyList<CommandRun> _allRuns = [];
    private IReadOnlyList<CommandRun> _pending = [];
    private CommandRun? _selected;
    private AnsiBuffer _buffer = new();
    private long _lastSeq;
    private long _loading;
    private long _tab = SystemTab;
    private bool _tabChosen;
    private bool _followNew = true;
    private bool _scroll;
    private bool _forceScroll;
    private bool _busy;
    private string? _error;
    private IReadOnlyList<TerminalInfo> _terminalInfos = [];
    private TerminalInfo? _terminal;

    [Parameter]
    public long? RunId { get; set; }

    /// <summary>A terminal to show: /console/terminal/{id}.</summary>
    [Parameter]
    public long? TerminalRef { get; set; }

    [Inject]
    private ICommandRuns Runs { get; set; } = default!;

    [Inject]
    private IWorkspaceStore WorkspaceStore { get; set; } = default!;

    [Inject]
    private IJSRuntime Js { get; set; } = default!;

    [Inject]
    private ISnackbar Snackbar { get; set; } = default!;

    [Inject]
    private TimeProvider Time { get; set; } = default!;

    [Inject]
    private ILogger<CommandConsole> Logger { get; set; } = default!;

    [Inject]
    private ITerminals Terminals { get; set; } = default!;

    // The history: one-off runs. A terminal's runs are shown in its terminal.
    private IReadOnlyList<CommandRun> AllTabRuns => _allRuns
        .Where(r => r.TerminalId is null && (_tab == SystemTab ? r.WorkspaceId is null : r.WorkspaceId == _tab))
        .Take(TabLimit)
        .ToList();

    // The terminals of the tab: running ones first, then by name.
    private IReadOnlyList<TerminalInfo> TabTerminals => _terminalInfos
        .Where(t => _tab == SystemTab ? t.Terminal.WorkspaceId is null : t.Terminal.WorkspaceId == _tab)
        .OrderByDescending(t => t.Terminal.State.IsLive())
        .ThenBy(t => t.Terminal.Name, StringComparer.Ordinal)
        .ToList();

    // What the list shows: the runs of the tab that match the filter box.
    private IReadOnlyList<CommandRun> TabRuns
    {
        get
        {
            var filter = _runFilter.Trim();
            return filter.Length == 0 ? AllTabRuns : AllTabRuns.Where(r => RunMatches(r, filter)).ToList();
        }
    }

    private IReadOnlyList<TabInfo> Tabs => _workspaces
        .Select(w => new TabInfo(w.Id, w.Name, ActiveIn(w.Id), LiveIn(w.Id)))
        .Append(new TabInfo(SystemTab, "System", ActiveIn(null), LiveIn(null)))
        .ToList();

    protected override async Task OnInitializedAsync()
    {
        await LoadWorkspacesAsync();
        await LoadRunsAsync();
        await LoadTerminalsAsync();
        RefreshOn<CommandRunChanged>(OnRunChangedAsync);
        RefreshOn<CommandOutputChunk>(OnChunkAsync);
        RefreshOn<WorkspaceChanged>(LoadWorkspacesAsync);
        RefreshOn<TerminalChanged>(OnTerminalChangedAsync);
        _ = TickAsync();
    }

    protected override async Task OnParametersSetAsync()
    {
        if (TerminalRef is { } terminalId)
        {
            if (_terminal?.Terminal.Id != terminalId)
            {
                await SelectTerminalAsync(terminalId, byUser: true);
            }

            return;
        }

        if (RunId is { } id)
        {
            if (_selected?.Id != id || _terminal is not null)
            {
                await SelectRunAsync(id, byUser: true);
            }

            return;
        }

        if (_selected is null && _terminal is null)
        {
            var live = _terminalInfos.FirstOrDefault(t => t.Terminal.State.IsLive());
            var first = _allRuns.FirstOrDefault(r => r.Status.IsActive() && r.TerminalId is null);
            if (first is not null)
            {
                await SelectRunAsync(first.Id, byUser: false);
            }
            else if (live is not null)
            {
                await SelectTerminalAsync(live.Terminal.Id, byUser: false);
            }
            else if (_allRuns.FirstOrDefault() is { } newest)
            {
                await SelectRunAsync(newest.Id, byUser: false);
            }
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            await LoadWrapPreferenceAsync();
        }

        // New output arrived: mark its matching lines too, without moving the reader.
        if (_findInfo.Length > 0)
        {
            await MarkAsync();
        }

        if (!_scroll)
        {
            return;
        }

        var force = _forceScroll;
        _scroll = false;
        _forceScroll = false;
        try
        {
            await Js.InvokeVoidAsync("wmConsole.follow", Cancel, TerminalId, force);
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException or OperationCanceledException)
        {
            Logger.LogDebug(ex, "The console could not be scrolled");
        }
    }

    private async Task LoadWorkspacesAsync()
    {
        try
        {
            _workspaces = (await WorkspaceStore.ListAsync(Cancel)).Where(w => w.Enabled).OrderBy(w => w.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "The Console page could not load the workspaces");
            _error = "The workspaces could not be loaded. The page will try again.";
        }
    }

    private async Task LoadRunsAsync()
    {
        try
        {
            _allRuns = await Runs.ListAsync(new CommandRunQuery(Limit: ListLimit), Cancel);
            _pending = await Runs.ListPendingAsync(Cancel);
            _error = null;
            if (!_tabChosen && _allRuns.Count > 0)
            {
                var newest = _allRuns.FirstOrDefault(r => r.Status.IsActive()) ?? _allRuns[0];
                _tab = newest.WorkspaceId ?? SystemTab;
                _tabChosen = true;
            }
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "The Console page could not load the commands");
            _error = "The commands could not be loaded. The page will try again.";
        }
    }

    private async Task LoadTerminalsAsync()
    {
        try
        {
            _terminalInfos = await Terminals.ListAsync(new TerminalQuery(), Cancel);
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "The Console page could not load the terminals");
            _error = "The terminals could not be loaded. The page will try again.";
        }
    }

    private async Task OnTerminalChangedAsync(TerminalChanged change)
    {
        await LoadTerminalsAsync();
        if (_terminal?.Terminal.Id != change.TerminalId)
        {
            return;
        }

        if (change.Closed)
        {
            _terminal = null;
            _selected = null;
            _buffer = new AnsiBuffer();
            return;
        }

        var info = _terminalInfos.FirstOrDefault(t => t.Terminal.Id == change.TerminalId) ?? await Terminals.GetAsync(change.TerminalId, Cancel);
        if (info is null)
        {
            return;
        }

        // A new program started in the terminal: show it from its start, with the earlier runs above it.
        var shownRun = info.Terminal.CurrentRunId ?? info.Terminal.LastRunId;
        if (shownRun != _selected?.Id)
        {
            await SelectTerminalAsync(change.TerminalId, byUser: false);
            return;
        }

        _terminal = info;
        if (shownRun is { } runId)
        {
            _selected = await Runs.GetAsync(runId, Cancel);
        }
    }

    private async Task OnRunChangedAsync(CommandRunChanged change)
    {
        await LoadRunsAsync();
        if (_selected?.Id == change.RunId)
        {
            _selected = await Runs.GetAsync(change.RunId, Cancel);
            return;
        }

        var isNew = change.Status is CommandRunStatus.WaitingApproval or CommandRunStatus.Queued or CommandRunStatus.Running;
        if (_followNew && isNew && (_selected is null || _selected.Id < change.RunId))
        {
            var run = await Runs.GetAsync(change.RunId, Cancel);
            if (run?.TerminalId is { } terminalId)
            {
                await SelectTerminalAsync(terminalId, byUser: false);
            }
            else
            {
                await SelectRunAsync(change.RunId, byUser: false);
            }
        }
    }

    private Task OnChunkAsync(CommandOutputChunk chunk)
    {
        if (chunk.RunId == _loading)
        {
            _early.Add(chunk);
        }
        else if (chunk.RunId == _selected?.Id && chunk.Seq > _lastSeq)
        {
            // Claude cleared the terminal: the view starts again after the marker line.
            var text = chunk.Text;
            if (_terminal is not null && AfterClear(text) is { } rest)
            {
                _buffer = new AnsiBuffer();
                text = rest;
            }

            _buffer.Write(text);
            _lastSeq = chunk.Seq;
            _scroll = true;
        }

        return Task.CompletedTask;
    }

    private async Task SelectTerminalAsync(long id, bool byUser)
    {
        if (byUser)
        {
            _followNew = false;
        }

        try
        {
            var info = await Terminals.GetAsync(id, Cancel);
            if (info is null)
            {
                _error = "That terminal does not exist any more.";
                return;
            }

            var shownRun = info.Terminal.CurrentRunId ?? info.Terminal.LastRunId;
            _loading = shownRun ?? 0;
            _early.Clear();
            var output = await Terminals.GetOutputAsync(id, Cancel);
            var run = output?.RunId is { } runId ? await Runs.GetAsync(runId, Cancel) : null;
            var buffer = new AnsiBuffer();
            var seq = output?.Seq ?? 0;
            var text = output?.Text ?? string.Empty;
            buffer.Write(AfterClear(text) ?? text);
            foreach (var chunk in _early.Where(c => c.RunId == run?.Id && c.Seq > seq).OrderBy(c => c.Seq))
            {
                buffer.Write(chunk.Text);
                seq = chunk.Seq;
            }

            _terminal = info;
            _selected = run;
            _buffer = buffer;
            _lastSeq = seq;
            _tab = info.Terminal.WorkspaceId ?? SystemTab;
            _tabChosen = true;
            _scroll = true;
            _forceScroll = true;
            _findInfo = string.Empty;
            _error = null;
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "The Console page could not load terminal {TerminalId}", id);
            _error = "That terminal could not be loaded.";
        }
        finally
        {
            _loading = 0;
            _early.Clear();
        }
    }

    private async Task SelectRunAsync(long id, bool byUser)
    {
        if (byUser)
        {
            _followNew = false;
        }

        _loading = id;
        _early.Clear();
        try
        {
            var run = await Runs.GetAsync(id, Cancel);

            // A run of a terminal is shown in its terminal, after the runs before it.
            if (run?.TerminalId is { } terminalId && await Terminals.GetAsync(terminalId, Cancel) is not null)
            {
                _loading = 0;
                await SelectTerminalAsync(terminalId, byUser);
                return;
            }

            _terminal = null;
            var snapshot = run is null ? null : await Runs.GetOutputAsync(id, Cancel);
            if (_loading != id)
            {
                return;
            }

            var buffer = new AnsiBuffer();
            var seq = 0L;
            if (snapshot is not null)
            {
                buffer.Write(snapshot.Text);
                seq = snapshot.Seq;
            }

            foreach (var chunk in _early.Where(c => c.Seq > seq).OrderBy(c => c.Seq))
            {
                buffer.Write(chunk.Text);
                seq = chunk.Seq;
            }

            _selected = run;
            _buffer = buffer;
            _lastSeq = seq;
            if (run is not null)
            {
                _tab = run.WorkspaceId ?? SystemTab;
                _tabChosen = true;
            }

            _scroll = true;
            _forceScroll = true;
            _findInfo = string.Empty;
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "The Console page could not load run {RunId}", id);
            _error = "That command could not be loaded.";
        }
        finally
        {
            if (_loading == id)
            {
                _loading = 0;
                _early.Clear();
            }
        }
    }

    private async Task SelectTabAsync(long tab)
    {
        _tab = tab;
        _tabChosen = true;

        // Nothing of the previous tab stays on screen while the new one loads.
        _selected = null;
        _terminal = null;
        _buffer = new AnsiBuffer();
        _lastSeq = 0;
        var live = TabTerminals.FirstOrDefault(t => t.Terminal.State.IsLive());
        var first = AllTabRuns.FirstOrDefault(r => r.Status.IsActive()) ?? AllTabRuns.FirstOrDefault();
        if (first is not null && (first.Status.IsActive() || live is null))
        {
            await SelectRunAsync(first.Id, byUser: true);
            return;
        }

        if (live is not null)
        {
            await SelectTerminalAsync(live.Terminal.Id, byUser: true);
            return;
        }

        _followNew = false;
    }

    private async Task ToggleFollowAsync()
    {
        _followNew = !_followNew;
        if (!_followNew)
        {
            return;
        }

        var newest = _allRuns.FirstOrDefault(r => r.Status.IsActive()) ?? _allRuns.FirstOrDefault();
        if (newest is not null && newest.Id != _selected?.Id)
        {
            await SelectRunAsync(newest.Id, byUser: false);
        }
    }

    private async Task OnDecidedAsync() => await LoadRunsAsync();

    private async Task StopAsync()
    {
        if (_selected is not { } run && _terminal is null)
        {
            return;
        }

        _busy = true;
        try
        {
            var stopped = _terminal is { } terminal
                ? await Terminals.StopAsync(terminal.Terminal.Id, TerminalActors.Owner, Cancel)
                : await Runs.StopAsync(_selected!.Id, Cancel);
            if (!stopped)
            {
                Snackbar.Add(_terminal is null ? "That command had already ended." : "Nothing runs in that terminal now.", Severity.Info);
            }
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task CopyOutputAsync()
    {
        if (_selected is null && _terminal is null)
        {
            return;
        }

        try
        {
            string text;
            if (_terminal is { } terminal)
            {
                var output = await Terminals.GetOutputAsync(terminal.Terminal.Id, Cancel);
                text = output?.Text ?? string.Empty;
            }
            else
            {
                var snapshot = await Runs.GetOutputAsync(_selected!.Id, Cancel);
                text = snapshot?.Text ?? string.Empty;
            }

            await Js.InvokeVoidAsync("navigator.clipboard.writeText", Cancel, AnsiText.Plain(text));
            Snackbar.Add("Output copied.", Severity.Success);
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException)
        {
            Snackbar.Add("Copying is blocked by the browser. Select the text and copy it by hand.", Severity.Warning);
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
        }
    }

    private Task ToEndAsync()
    {
        _scroll = true;
        _forceScroll = true;
        return Task.CompletedTask;
    }

    private int ActiveIn(long? workspaceId) => _allRuns.Count(r => r.TerminalId is null && r.Status.IsActive() && r.WorkspaceId == workspaceId);

    private int LiveIn(long? workspaceId) => _terminalInfos.Count(t => t.Terminal.State.IsLive() && t.Terminal.WorkspaceId == workspaceId);

    // The text after the last "cleared" marker line, or null when there is none.
    private static string? AfterClear(string text)
    {
        var at = text.LastIndexOf(Waymark.Modules.Commands.CommandCoordinator.ClearedMarker, StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }

        var newline = text.IndexOf('\n', at);
        return newline < 0 ? string.Empty : text[(newline + 1)..];
    }

    private string ReadLine(TerminalInfo info)
    {
        if (info.ReadUtc is not { } read)
        {
            return info.Terminal.State.IsLive() ? "Claude has not read it yet" : string.Empty;
        }

        var ago = DisplayFormat.Ago(read, Time.GetUtcNow());
        return info.TotalLines > 0
            ? $"Claude read {info.ReadLine} of {info.TotalLines} lines {ago}"
            : $"Claude read it {ago}";
    }

    private static string Label(CommandRun run) =>
        CommandCatalog.TryGet(run.Operation, out var info) && info is not null ? info.Label : run.Operation;

    private static string Short(string commandLine) => CommandLineDisplay.Compact(commandLine, ShortChars);

    private static string DotClass(CommandRun run) => run.Status switch
    {
        CommandRunStatus.Succeeded => "ok",
        CommandRunStatus.Failed or CommandRunStatus.TimedOut => "err",
        CommandRunStatus.Running => "run",
        CommandRunStatus.Queued or CommandRunStatus.WaitingApproval => "warn",
        _ => "off",
    };

    private static (StatusState State, string Text) Chip(CommandRun run) => run.Status switch
    {
        CommandRunStatus.Succeeded => (StatusState.Healthy, "Succeeded"),
        CommandRunStatus.Failed => (StatusState.Failed, run.ExitCode is { } code ? $"Failed (exit {code})" : "Failed"),
        CommandRunStatus.TimedOut => (StatusState.Failed, "Timed out"),
        CommandRunStatus.Running => (StatusState.Working, "Running"),
        CommandRunStatus.Queued => (StatusState.Attention, "Queued"),
        CommandRunStatus.WaitingApproval => (StatusState.Attention, "Waiting for you"),
        CommandRunStatus.Denied => (StatusState.Off, "Denied"),
        _ => (StatusState.Off, "Stopped"),
    };

    private string Elapsed(CommandRun run)
    {
        if (run.StartedUtc is not { } start)
        {
            return run.Status == CommandRunStatus.WaitingApproval ? "not started" : "0.0 s";
        }

        var seconds = ((run.EndedUtc ?? Time.GetUtcNow()) - start).TotalSeconds;
        return seconds < 60 ? $"{seconds:0.0} s" : $"{(int)(seconds / 60)} min {(int)(seconds % 60)} s";
    }

    // Redraws once a second while something is waiting or running, so times and the expiry countdown move.
    private async Task TickAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(Cancel))
            {
                if (_pending.Count > 0 || _selected?.Status.IsActive() == true || _terminal?.Terminal.State.IsLive() == true)
                {
                    await InvokeAsync(StateHasChanged);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
        }
        catch (ObjectDisposedException)
        {
            // The circuit ended.
        }
    }

    private sealed record TabInfo(long Id, string Name, int Active, int Live);
}
