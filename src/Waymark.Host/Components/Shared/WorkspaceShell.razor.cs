using Microsoft.AspNetCore.Components;
using Waymark.Core.Code;
using Waymark.Core.Commands;
using Waymark.Core.Events;
using Waymark.Core.Memory;
using Waymark.Core.Workspaces;
using Waymark.Host.Ui;

namespace Waymark.Host.Components.Shared;

/// <summary>
/// The frame of every workspace page: the way back, the name, status and quick actions of the workspace, and the menu of
/// its sections with the flow and note counts. The content of the section goes inside and receives the counts as a cascading value.
/// The counts reload when a flow or note of this workspace changes, or agents used its memory.
/// </summary>
public partial class WorkspaceShell : LiveComponentBase
{
    private Workspace? _workspace;
    private GitHubRepo? _repo;
    private CodeIndexProgress? _progress;
    private MemoryCounts? _counts;
    private string? _pathRoot;
    private string? _error;
    private long _shownId;
    private bool _loaded;
    private int _liveTerminals;
    private int _crashedTerminals;

    [Parameter, EditorRequired]
    public long WorkspaceId { get; set; }

    /// <summary>The page of the section. It receives the workspace and is shown only while the workspace exists.</summary>
    [Parameter]
    public RenderFragment<Workspace>? ChildContent { get; set; }

    [Inject]
    private IWorkspaceService Service { get; set; } = default!;

    [Inject]
    private NavigationManager Navigation { get; set; } = default!;

    [Inject]
    private IMemoryStatsStore MemoryStats { get; set; } = default!;

    [Inject]
    private ILogger<WorkspaceShell> Logger { get; set; } = default!;

    [Inject]
    private ITerminals Terminals { get; set; } = default!;

    [Inject]
    private TimeProvider Time { get; set; } = default!;

    protected override void OnInitialized()
    {
        RefreshOn<WorkspaceChanged>(LoadAsync);
        RefreshOn<CodeIndexProgress>(OnProgressAsync);
        RefreshOn<FeatureMapsChanged>(changed => changed.WorkspaceId == WorkspaceId ? LoadCountsAsync() : Task.CompletedTask);
        RefreshOn<NoteChanged>(changed => changed.WorkspaceId == WorkspaceId ? LoadCountsAsync() : Task.CompletedTask);
        RefreshOn<MemoryUsageRecorded>(recorded => recorded.WorkspaceIds.Contains(WorkspaceId) ? LoadCountsAsync() : Task.CompletedTask);
        RefreshOn<TerminalChanged>(changed => changed.WorkspaceId == WorkspaceId ? LoadTerminalsAsync() : Task.CompletedTask);
    }

    private async Task LoadTerminalsAsync()
    {
        try
        {
            var terminals = await Terminals.ListAsync(new TerminalQuery(WorkspaceId: WorkspaceId), Cancel);
            var now = Time.GetUtcNow();
            _liveTerminals = terminals.Count(t => t.Terminal.State.IsLive());
            _crashedTerminals = terminals.Count(t => t.Terminal.State == TerminalState.Crashed && t.Terminal.EndedUtc is { } ended && now - ended < TimeSpan.FromHours(1));
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "The terminals of workspace {WorkspaceId} could not be loaded", WorkspaceId);
        }
    }

    protected override async Task OnParametersSetAsync()
    {
        if (_shownId != WorkspaceId)
        {
            _shownId = WorkspaceId;
            _workspace = null;
            _repo = null;
            _progress = null;
            _counts = null;
            _loaded = false;
        }

        await LoadAsync();
        await LoadCountsAsync();
        await LoadTerminalsAsync();
    }

    private async Task LoadCountsAsync()
    {
        try
        {
            _counts = await MemoryStats.GetAsync(WorkspaceId, Cancel);
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
        }
        catch (Exception ex)
        {
            // The tabs then show their titles without counts; the section itself still works.
            Logger.LogWarning(ex, "The memory counts of workspace {WorkspaceId} could not be loaded", WorkspaceId);
        }
    }

    private Task OnProgressAsync(CodeIndexProgress progress)
    {
        if (progress.WorkspaceId != WorkspaceId)
        {
            return Task.CompletedTask;
        }

        _progress = progress;

        // A finished run changes the time of the last index.
        return progress.Phase == CodeIndexPhase.Completed ? LoadAsync() : Task.CompletedTask;
    }

    private async Task LoadAsync()
    {
        try
        {
            var all = await Service.ListAsync(Cancel);
            var workspace = all.FirstOrDefault(item => item.Id == WorkspaceId);
            GitHubRepo? repo = null;
            if (workspace is not null)
            {
                repo = await Task.Run(() => GitHubRepoDetector.Detect(workspace.Path), Cancel);
            }

            _workspace = workspace;
            _repo = repo;
            _pathRoot = WorkspacePathDisplay.CommonParent(all.Select(item => item.Path));
            _error = null;
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
            return;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "The workspace {WorkspaceId} could not be loaded", WorkspaceId);
            _error = "The workspace could not be loaded.";
        }

        _loaded = true;
    }

    private void OnRemoved() => Navigation.NavigateTo("/workspaces");
}
