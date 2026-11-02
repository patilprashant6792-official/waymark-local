using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using MudBlazor;
using Waymark.Core.Events;
using Waymark.Core.Memory;
using Waymark.Core.Workspaces;
using Waymark.Host.Components.Shared;
using Waymark.Host.FeatureMaps;
using Waymark.Host.Ui;

namespace Waymark.Host.Components.Pages;

/// <summary>
/// The Feature maps section of a workspace: the maps Claude saved, whether each component is in the code that is checked out, and removing what is wrong.
/// It reloads when Claude saves or deletes a map.
/// </summary>
public partial class FeatureMapList : LiveComponentBase
{
    private static readonly IReadOnlyDictionary<string, FeatureComponentStatus> NoStatus = new Dictionary<string, FeatureComponentStatus>();

    private IReadOnlyList<FeatureMap> _maps = [];
    private IReadOnlyDictionary<string, FeatureComponentStatus> _status = NoStatus;
    private DateTimeOffset? _checkedUtc;
    private string _query = string.Empty;
    private string _searchedFor = string.Empty;
    private string? _error;
    private long _shownId;
    private int _total;
    private bool _loaded;
    private bool _busy;

    [Parameter, EditorRequired]
    public Workspace Workspace { get; set; } = default!;

    [Inject]
    private FeatureMapAdminService Service { get; set; } = default!;

    [Inject]
    private IDialogService Dialogs { get; set; } = default!;

    [Inject]
    private ISnackbar Snackbar { get; set; } = default!;

    [Inject]
    private TimeProvider Time { get; set; } = default!;

    [Inject]
    private ILogger<FeatureMapList> Logger { get; set; } = default!;

    protected override void OnInitialized() => RefreshOn<FeatureMapsChanged>(OnChangedAsync);

    // The parent renders again whenever the workspace index moves; only a different workspace reloads the maps.
    protected override async Task OnParametersSetAsync()
    {
        if (_shownId == Workspace.Id)
        {
            return;
        }

        _shownId = Workspace.Id;
        _query = string.Empty;
        _searchedFor = string.Empty;
        _maps = [];
        _status = NoStatus;
        _checkedUtc = null;
        _loaded = false;
        await LoadAsync();
    }

    private Task OnChangedAsync(FeatureMapsChanged changed) => changed.WorkspaceId == Workspace.Id ? LoadAsync() : Task.CompletedTask;

    private async Task LoadAsync()
    {
        try
        {
            var view = await Service.LoadAsync(Workspace.Id, _searchedFor, Cancel);
            _maps = view.Maps;
            _total = view.Total;
            _error = null;
            _status = await Service.CheckAsync(Workspace, _maps, Cancel);
            _checkedUtc = Time.GetUtcNow();
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
            return;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "The feature maps of workspace {WorkspaceId} could not be loaded or checked", Workspace.Id);
            _error = "The feature maps could not be loaded or checked.";
        }

        _loaded = true;
    }

    private Task SearchAsync()
    {
        _searchedFor = _query.Trim();
        return RunBusyAsync(LoadAsync, "The feature maps could not be searched.");
    }

    private Task ShowAllAsync()
    {
        _query = string.Empty;
        _searchedFor = string.Empty;
        return RunBusyAsync(LoadAsync, "The feature maps could not be loaded.");
    }

    private async Task OnSearchKey(KeyboardEventArgs args)
    {
        if (args.Key == "Enter" && !string.IsNullOrWhiteSpace(_query) && !_busy)
        {
            await SearchAsync();
        }
    }

    private Task CheckAsync() => RunBusyAsync(
        async () =>
        {
            _status = await Service.CheckAsync(Workspace, _maps, Cancel);
            _checkedUtc = Time.GetUtcNow();
            _error = null;
        },
        "The components could not be checked against the code.");

    private async Task DeleteMapAsync(FeatureMap map)
    {
        var confirmed = await ConfirmDialogs.AskAsync(
            Dialogs,
            "Delete this feature map?",
            $"'{map.Name}' and its {map.Components.Count} components are removed. Claude can save it again after exploring the feature.",
            "Delete",
            destructive: true);
        if (!confirmed)
        {
            return;
        }

        await RunBusyAsync(
            async () =>
            {
                if (await Service.DeleteAsync(Workspace, map.Name, Cancel))
                {
                    Snackbar.Add($"Deleted '{map.Name}'.", Severity.Success);
                }
            },
            "The feature map could not be deleted.");
    }

    private async Task RemoveComponentAsync(FeatureMap map, FeatureMapComponent component)
    {
        var confirmed = await ConfirmDialogs.AskAsync(
            Dialogs,
            "Remove this component?",
            $"'{component.Spec}' is removed from '{map.Name}'. Claude can add it again.",
            "Remove",
            destructive: true);
        if (!confirmed)
        {
            return;
        }

        await RunBusyAsync(
            async () =>
            {
                await Service.RemoveComponentAsync(Workspace, map.Name, component.Spec, Cancel);
                Snackbar.Add("Removed.", Severity.Success);
            },
            "The component could not be removed.");
    }

    private async Task RunBusyAsync(Func<Task> work, string failure)
    {
        _busy = true;
        try
        {
            await work();
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "{Failure}", failure);
            _error = failure;
        }
        finally
        {
            _busy = false;
        }
    }

    private FeatureComponentStatus? StatusOf(string spec) => _status.TryGetValue(spec, out var status) ? status : null;

    private int MissingCount(FeatureMap map) => map.Components.Count(component => _status.TryGetValue(component.Spec, out var status) && !status.Available);

    // The same text memorise takes, so a map can be pasted into a note or saved again.
    private static string ComponentsText(FeatureMap map) =>
        string.Join("\n", map.Components.Select(component => component.Note.Length == 0 ? component.Spec : $"{component.Spec} | {component.Note}"));

    private string Caption()
    {
        var count = $"{DisplayFormat.Count(_total)} {(_total == 1 ? "map" : "maps")}";
        var matching = _searchedFor.Length > 0 ? $" match '{_searchedFor}'" : string.Empty;
        var shown = _total > _maps.Count ? $", the newest {_maps.Count} shown (search to find the rest)" : string.Empty;
        var checkedText = _checkedUtc is { } at ? $" Checked against the code {DisplayFormat.Ago(at, Time.GetUtcNow())}." : string.Empty;
        return $"{count}{matching}{shown}.{checkedText}";
    }
}
