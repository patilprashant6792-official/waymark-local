using Microsoft.AspNetCore.Components;
using Waymark.Host.Ui;
using MudBlazor;
using Waymark.Core.Code;
using Waymark.Core.Events;
using Waymark.Core.Workspaces;
using Waymark.Host.Components.Shared;
using Waymark.Modules.Code.Scheduling;

namespace Waymark.Host.Components.Pages;

public partial class Workspaces : LiveComponentBase
{
    private readonly Dictionary<long, CodeIndexStats> _stats = [];
    private readonly Dictionary<long, CodeIndexProgress> _progress = [];
    private IReadOnlyList<Workspace> _items = [];
    private WorkspaceValidation? _check;
    private string _name = string.Empty;
    private string _path = string.Empty;
    private string? _error;
    private string? _pathRoot;
    private bool _busy;

    [Inject]
    private IWorkspaceService Service { get; set; } = default!;

    [Inject]
    private ICodeIndexStore Index { get; set; } = default!;

    [Inject]
    private IIndexScheduler Scheduler { get; set; } = default!;

    [Inject]
    private IDialogService Dialogs { get; set; } = default!;

    [Inject]
    private ISnackbar Snackbar { get; set; } = default!;

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

    private Task OnProgressAsync(CodeIndexProgress progress)
    {
        _progress[progress.WorkspaceId] = progress;
        return LoadStatsAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            _items = await Service.ListAsync(Cancel);
            _pathRoot = WorkspacePathDisplay.CommonParent(_items.Select(item => item.Path));
            await LoadStatsAsync();
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "The workspaces could not be loaded");
            _error = "The workspaces could not be loaded.";
        }
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

    private async Task CheckAsync()
    {
        _error = null;
        try
        {
            _check = await Service.ValidateAsync(_path, Cancel);
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
        }
    }

    private async Task AddAsync()
    {
        _error = null;
        _busy = true;
        try
        {
            var result = await Service.AddAsync(_name, _path, Cancel);
            if (!result.IsSuccess || result.Workspace is null)
            {
                _error = result.Error ?? "The workspace could not be added.";
                return;
            }

            Scheduler.RequestReconcile(result.Workspace.Id);
            Snackbar.Add($"Added '{result.Workspace.Name}'. Indexing has started.", Severity.Success);
            _name = string.Empty;
            _path = string.Empty;
            _check = null;
            await LoadAsync();
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

    private async Task ToggleAsync(Workspace workspace, bool enabled)
    {
        try
        {
            await Service.SetEnabledAsync(workspace.Id, enabled, Cancel);
            if (enabled)
            {
                Scheduler.RequestReconcile(workspace.Id);
            }
            else
            {
                Scheduler.Forget(workspace.Id);
            }

            await LoadAsync();
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
        }
    }

    private void Reindex(Workspace workspace)
    {
        Scheduler.RequestRebuild(workspace.Id);
        Snackbar.Add($"Re-indexing '{workspace.Name}'.", Severity.Info);
    }

    private async Task RemoveAsync(Workspace workspace)
    {
        var confirmed = await ConfirmDialogs.AskAsync(
            Dialogs,
            "Remove this workspace?",
            $"'{workspace.Name}' is forgotten and its index is deleted. Your files are not touched.",
            "Remove",
            destructive: true);
        if (!confirmed)
        {
            return;
        }

        try
        {
            Scheduler.Forget(workspace.Id);
            await Service.RemoveAsync(workspace.Id, Cancel);
            await LoadAsync();
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
        }
    }

    private (StatusState State, string Text) StateOf(Workspace workspace)
    {
        if (!workspace.Enabled)
        {
            return (StatusState.Off, "Disabled");
        }

        if (Event(workspace.Id) is { Phase: CodeIndexPhase.Failed })
        {
            return (StatusState.Failed, "Index failed");
        }

        return IsIndexing(workspace.Id) ? (StatusState.Working, "Indexing") : (StatusState.Healthy, "Ready");
    }

    private CodeIndexProgress? Event(long id) => _progress.GetValueOrDefault(id);

    private bool IsIndexing(long id) => Event(id) is { Phase: CodeIndexPhase.Started or CodeIndexPhase.Running };

    private string ProgressText(long id) =>
        Event(id) is { Phase: CodeIndexPhase.Started or CodeIndexPhase.Running } p
            ? $"{DisplayFormat.Count(p.FilesProcessed)} of {DisplayFormat.Count(p.FilesTotal)} files"
            : string.Empty;

    private static string StatsText(CodeIndexStats? stats)
    {
        if (stats is null)
        {
            return "-";
        }

        var text = $"{DisplayFormat.Count(stats.Files)} files, {DisplayFormat.Count(stats.Types)} types, {DisplayFormat.Count(stats.Members)} members";
        if (stats.FilesByLanguage is { Count: > 0 } languages && (languages.Count > 1 || !languages.ContainsKey(CodeLanguage.CSharp)))
        {
            var parts = languages.OrderByDescending(pair => pair.Value).Select(pair => $"{CodeLanguages.DisplayName(pair.Key)} {DisplayFormat.Count(pair.Value)}");
            text += " (" + string.Join(", ", parts) + ")";
        }

        return text;
    }
}
