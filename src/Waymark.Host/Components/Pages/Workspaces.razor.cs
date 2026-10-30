using Microsoft.AspNetCore.Components;
using MudBlazor;
using Waymark.Core.Code;
using Waymark.Core.Events;
using Waymark.Core.Workspaces;
using Waymark.Host.Components.Shared;
using Waymark.Host.Ui;

namespace Waymark.Host.Components.Pages;

public partial class Workspaces : LiveComponentBase
{
    private readonly Dictionary<long, CodeIndexStats> _stats = [];
    private readonly Dictionary<long, CodeIndexProgress> _progress = [];
    private Dictionary<long, GitHubRepo> _repos = [];
    private IReadOnlyList<Workspace> _items = [];
    private string? _error;
    private string? _pathRoot;
    private bool _loaded;

    [Inject]
    private IWorkspaceService Service { get; set; } = default!;

    [Inject]
    private ICodeIndexStore Index { get; set; } = default!;

    [Inject]
    private IDialogService Dialogs { get; set; } = default!;

    [Inject]
    private TimeProvider Time { get; set; } = default!;

    [Inject]
    private ILogger<Workspaces> Logger { get; set; } = default!;

    protected override async Task OnInitializedAsync()
    {
        await LoadAsync();
        RefreshOn<WorkspaceChanged>(LoadAsync);
        RefreshOn<CodeIndexProgress>(OnProgressAsync);
    }

    private async Task OnProgressAsync(CodeIndexProgress progress)
    {
        _progress[progress.WorkspaceId] = progress;

        // A finished run changes the time of the last index, which lives on the workspace.
        if (progress.Phase == CodeIndexPhase.Completed)
        {
            await LoadAsync();
            return;
        }

        await LoadStatsAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            _items = await Service.ListAsync(Cancel);
            _pathRoot = WorkspacePathDisplay.CommonParent(_items.Select(item => item.Path));
            _repos = await WorkspaceView.DetectReposAsync(_items, Cancel);
            await LoadStatsAsync();
            _error = null;
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
            return;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "The workspaces could not be loaded");
            _error = "The workspaces could not be loaded.";
        }

        _loaded = true;
    }

    private string ShownPath(string path) => WorkspacePathDisplay.Relative(path, _pathRoot);

    private async Task LoadStatsAsync()
    {
        try
        {
            foreach (var item in _items)
            {
                _stats[item.Id] = await Index.GetStatsAsync(item.Id, Cancel);
            }
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
        }
    }

    private async Task AddAsync()
    {
        var options = new DialogOptions { CloseOnEscapeKey = true, MaxWidth = MaxWidth.Medium, FullWidth = true };
        await Dialogs.ShowAsync<AddWorkspaceDialog>("Add a workspace", options);
    }
}
