using Microsoft.AspNetCore.Components;
using Waymark.Core.Code;
using Waymark.Core.Events;
using Waymark.Core.Workspaces;
using Waymark.Host.Ui;

namespace Waymark.Host.Components.Shared;

/// <summary>
/// The frame of every workspace page: the way back, the name, status and quick actions of the workspace, and the menu of
/// its sections. The content of the section goes inside.
/// </summary>
public partial class WorkspaceShell : LiveComponentBase
{
    private Workspace? _workspace;
    private GitHubRepo? _repo;
    private CodeIndexProgress? _progress;
    private string? _pathRoot;
    private string? _error;
    private long _shownId;
    private bool _loaded;

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
    private ILogger<WorkspaceShell> Logger { get; set; } = default!;

    protected override void OnInitialized()
    {
        RefreshOn<WorkspaceChanged>(LoadAsync);
        RefreshOn<CodeIndexProgress>(OnProgressAsync);
    }

    protected override async Task OnParametersSetAsync()
    {
        if (_shownId != WorkspaceId)
        {
            _shownId = WorkspaceId;
            _workspace = null;
            _repo = null;
            _progress = null;
            _loaded = false;
        }

        await LoadAsync();
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
