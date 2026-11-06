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
/// The Flows section of a workspace: the flows Claude saved as steps, whether each step is in the code that is checked out, whether each link still holds, and removing what is wrong.
/// It reloads when Claude saves or deletes a map.
/// </summary>
public partial class FeatureMapList : LiveComponentBase
{
    private static readonly IReadOnlyDictionary<long, IReadOnlyList<FeatureComponentStatus>> NoStatus = new Dictionary<long, IReadOnlyList<FeatureComponentStatus>>();

    private IReadOnlyList<FeatureMap> _maps = [];
    private IReadOnlyDictionary<long, IReadOnlyList<FeatureComponentStatus>> _status = NoStatus;
    private DateTimeOffset? _checkedUtc;
    private string _query = string.Empty;
    private string _searchedFor = string.Empty;
    private string? _error;
    private long _shownId;
    private int _total;
    private bool _loaded;
    private bool _busy;
    private bool _checking;

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

            // Show the maps at once: the check reads and parses every file the steps name, which takes seconds when there are many maps.
            _status = NoStatus;
            _checkedUtc = null;
            _checking = true;
            _loaded = true;
            await InvokeAsync(StateHasChanged);

            var maps = _maps;
            var status = await Service.CheckAsync(Workspace, maps, Cancel);

            // A newer load may have replaced the maps while this one was checking: its answer is for the old steps.
            if (ReferenceEquals(maps, _maps))
            {
                _status = status;
                _checkedUtc = Time.GetUtcNow();
            }
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
            return;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "The flows of workspace {WorkspaceId} could not be loaded or checked", Workspace.Id);
            _error = "The flows could not be loaded or checked.";
        }
        finally
        {
            _checking = false;
        }

        _loaded = true;
    }

    private Task SearchAsync()
    {
        _searchedFor = _query.Trim();
        return RunBusyAsync(LoadAsync, "The flows could not be searched.");
    }

    private Task ShowAllAsync()
    {
        _query = string.Empty;
        _searchedFor = string.Empty;
        return RunBusyAsync(LoadAsync, "The flows could not be loaded.");
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
        "The steps could not be checked against the code.");

    private async Task DeleteMapAsync(FeatureMap map)
    {
        var confirmed = await ConfirmDialogs.AskAsync(
            Dialogs,
            "Delete this flow?",
            $"'{map.Name}' and its {map.Steps.Count} steps are removed. Claude can save it again after exploring the feature.",
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
            "The flow could not be deleted.");
    }

    private async Task RemoveStepAsync(FeatureMap map, int index)
    {
        var step = map.Steps[index];
        var confirmed = await ConfirmDialogs.AskAsync(
            Dialogs,
            "Remove this step?",
            $"'{step.Spec}' is removed from '{map.Name}'. Claude can add it again.",
            "Remove",
            destructive: true);
        if (!confirmed)
        {
            return;
        }

        await RunBusyAsync(
            async () =>
            {
                try
                {
                    await Service.RemoveStepAsync(Workspace, map, index, Cancel);
                    Snackbar.Add("Removed.", Severity.Success);
                }
                catch (InvalidOperationException ex)
                {
                    // A refusal the owner can act on (the map changed, or the step has steps under it), not a failure.
                    Snackbar.Add(ex.Message, Severity.Warning);
                    await LoadAsync();
                }
            },
            "The step could not be removed.");
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

    private FeatureComponentStatus? StatusOf(FeatureMap map, int index) =>
        _status.TryGetValue(map.Id, out var statuses) && index < statuses.Count ? statuses[index] : null;

    private int MissingCount(FeatureMap map) =>
        _status.TryGetValue(map.Id, out var statuses) ? statuses.Count(status => !status.Available) : 0;

    private int BrokenCount(FeatureMap map) =>
        _status.TryGetValue(map.Id, out var statuses) ? statuses.Count(status => status.Link == FlowLinkState.DoesNotCall) : 0;

    private static string? WhyNotRemovable(FeatureMap map, int index) => FeatureMapAdminService.WhyNotRemovable(map, index);

    // The same text memorise takes (indented under the step that calls it), so a flow can be pasted into a note or saved again.
    private static string StepsText(FeatureMap map) => FlowStepText.Format(FlowStepText.FromStored(map.Steps));

    // The steps are paths: they wrap anywhere (see .wm-step), so a long path never pushes the actions out of sight. Depth maps to a fixed set of indent classes.
    private static string IndentClass(FeatureMapStep step) => $"wm-indent-{Math.Clamp(step.Depth, 0, 8)}";

    // What the code says about the link from the step above, in the words of the other states on this page. Never colour alone: each has a word.
    private static (StatusState State, string Text, string? Title)? LinkChip(FeatureComponentStatus? status)
    {
        if (status?.Link is not { } link)
        {
            return null;
        }

        return link switch
        {
            FlowLinkState.Calls => (StatusState.Healthy, "Calls it", "The step above calls this one."),
            FlowLinkState.NameOnly => (StatusState.Working, "By name only", status.LinkDetail),
            FlowLinkState.Declared => (StatusState.Off, "Declared", status.LinkDetail),
            FlowLinkState.DoesNotCall => (StatusState.Failed, "Does not call", status.LinkDetail),
            _ => (StatusState.Attention, "Not judged", status.LinkDetail),
        };
    }

    private string Caption()
    {
        var count = $"{DisplayFormat.Count(_total)} {(_total == 1 ? "map" : "maps")}";
        var matching = _searchedFor.Length > 0 ? $" match '{_searchedFor}'" : string.Empty;
        var shown = _total > _maps.Count ? $", the newest {_maps.Count} shown (search to find the rest)" : string.Empty;
        var checkedText = _checking
            ? " Checking against the code..."
            : _checkedUtc is { } at ? $" Checked against the code {DisplayFormat.Ago(at, Time.GetUtcNow())}." : string.Empty;
        return $"{count}{matching}{shown}.{checkedText}";
    }
}
