using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;
using Waymark.Core.Commands;
using Waymark.Core.Events;
using Waymark.Core.Workspaces;
using Waymark.Host.Components.Shared;
using Waymark.Host.Terminal;
using Waymark.Host.Ui;
using Waymark.Modules.Commands.Execution;

namespace Waymark.Host.Components.Pages;

public partial class CommandConsole : LiveComponentBase
{
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

    [Parameter]
    public long? RunId { get; set; }

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

    private IReadOnlyList<CommandRun> AllTabRuns => _allRuns
        .Where(r => _tab == SystemTab ? r.WorkspaceId is null : r.WorkspaceId == _tab)
        .Take(TabLimit)
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
        .Select(w => new TabInfo(w.Id, w.Name, ActiveIn(w.Id)))
        .Append(new TabInfo(SystemTab, "System", ActiveIn(null)))
        .ToList();

    protected override async Task OnInitializedAsync()
    {
        await LoadWorkspacesAsync();
        await LoadRunsAsync();
        RefreshOn<CommandRunChanged>(OnRunChangedAsync);
        RefreshOn<CommandOutputChunk>(OnChunkAsync);
        RefreshOn<WorkspaceChanged>(LoadWorkspacesAsync);
        _ = TickAsync();
    }

    protected override async Task OnParametersSetAsync()
    {
        if (RunId is { } id)
        {
            if (_selected?.Id != id)
            {
                await SelectRunAsync(id, byUser: true);
            }

            return;
        }

        if (_selected is null)
        {
            var first = _allRuns.FirstOrDefault(r => r.Status.IsActive()) ?? _allRuns.FirstOrDefault();
            if (first is not null)
            {
                await SelectRunAsync(first.Id, byUser: false);
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
            await SelectRunAsync(change.RunId, byUser: false);
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
            _buffer.Write(chunk.Text);
            _lastSeq = chunk.Seq;
            _scroll = true;
        }

        return Task.CompletedTask;
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
        var first = AllTabRuns.FirstOrDefault(r => r.Status.IsActive()) ?? AllTabRuns.FirstOrDefault();
        if (first is not null)
        {
            await SelectRunAsync(first.Id, byUser: true);
            return;
        }

        _followNew = false;
        _selected = null;
        _buffer = new AnsiBuffer();
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
        if (_selected is not { } run)
        {
            return;
        }

        _busy = true;
        try
        {
            if (!await Runs.StopAsync(run.Id, Cancel))
            {
                Snackbar.Add("That command had already ended.", Severity.Info);
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
        if (_selected is not { } run)
        {
            return;
        }

        try
        {
            var snapshot = await Runs.GetOutputAsync(run.Id, Cancel);
            await Js.InvokeVoidAsync("navigator.clipboard.writeText", Cancel, AnsiText.Plain(snapshot?.Text ?? string.Empty));
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

    private int ActiveIn(long? workspaceId) => _allRuns.Count(r => r.Status.IsActive() && r.WorkspaceId == workspaceId);

    private static string Label(CommandRun run) =>
        CommandCatalog.TryGet(run.Operation, out var info) && info is not null ? info.Label : run.Operation;

    private static string Short(string commandLine)
    {
        var line = commandLine.ReplaceLineEndings(" ").Trim();
        return line.Length <= ShortChars ? line : line[..ShortChars] + "...";
    }

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
                if (_pending.Count > 0 || _selected?.Status.IsActive() == true)
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

    private sealed record TabInfo(long Id, string Name, int Active);
}
