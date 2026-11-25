using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using MudBlazor;
using Waymark.Core.Events;
using Waymark.Core.Memory;
using Waymark.Core.Workspaces;
using Waymark.Host.Components.Shared;
using Waymark.Host.Components.Ui;
using Waymark.Host.FeatureMaps;
using Waymark.Host.Memory;
using Waymark.Host.Notes;
using Waymark.Host.Ui;

namespace Waymark.Host.Components.Pages;

/// <summary>
/// The Flows section of a workspace: the flows agents saved as steps, a page at a time, newest or last used first; whether each step is in the code that is
/// checked out, whether each link still holds, when an agent last opened each flow, and removing what is wrong. A flow's steps are built only while it is open,
/// and a long flow shows its steps in chunks. It reloads when a flow changes and refreshes the last-used times when agents used flows.
/// </summary>
public partial class FeatureMapList : LiveComponentBase
{
    /// <summary>How many steps the check after a load covers, in page order; the flows after that are checked when opened or with 'Check against the code'.</summary>
    internal const int CheckBudgetSteps = 3000;

    /// <summary>How many steps of one flow are shown before 'Show more'.</summary>
    internal const int StepChunk = 100;

    private static readonly IReadOnlyDictionary<long, IReadOnlyList<FeatureComponentStatus>> NoStatus = new Dictionary<long, IReadOnlyList<FeatureComponentStatus>>();

    private static readonly IReadOnlyList<WmOption<MemoryOrder>> OrderOptions =
    [
        new(MemoryOrder.Changed, "Last changed"),
        new(MemoryOrder.Used, "Last used"),
    ];

    private readonly Dictionary<long, int> _stepsShown = [];
    private IReadOnlyList<FeatureMap> _maps = [];
    private IReadOnlyDictionary<long, IReadOnlyList<FeatureComponentStatus>> _status = NoStatus;
    private IReadOnlyDictionary<long, DateTimeOffset> _lastUsed = new Dictionary<long, DateTimeOffset>();
    private DateTimeOffset? _checkedUtc;
    private MemoryOrder _order = MemoryOrder.Changed;
    private int _page;
    private string _query = string.Empty;
    private string _searchedFor = string.Empty;
    private string? _error;
    private long _shownId;
    private int _total;
    private bool _loaded;
    private bool _busy;
    private bool _checking;
    private IReadOnlyList<Note> _notes = [];
    private IReadOnlyDictionary<long, NoteStaleness> _noteStaleness = new Dictionary<long, NoteStaleness>();
    private (long MapId, string? Spec)? _editorAt;
    private Note? _editingNote;
    private string? _noteError;
    private long? _historyOf;
    private IReadOnlyList<Note> _history = [];

    [Parameter, EditorRequired]
    public Workspace Workspace { get; set; } = default!;

    [Inject]
    private FeatureMapAdminService Service { get; set; } = default!;

    [Inject]
    private NoteAdminService Notes { get; set; } = default!;

    [Inject]
    private MemoryHealthCache Health { get; set; } = default!;

    [Inject]
    private IDialogService Dialogs { get; set; } = default!;

    [Inject]
    private ISnackbar Snackbar { get; set; } = default!;

    [Inject]
    private TimeProvider Time { get; set; } = default!;

    [Inject]
    private ILogger<FeatureMapList> Logger { get; set; } = default!;

    private int Pages => Math.Max(1, (_total + FeatureMapAdminService.PageSize - 1) / FeatureMapAdminService.PageSize);

    protected override void OnInitialized()
    {
        RefreshOn<FeatureMapsChanged>(OnChangedAsync);
        RefreshOn<NoteChanged>(changed => changed.WorkspaceId == Workspace.Id ? LoadNotesAsync() : Task.CompletedTask);
        RefreshOn<MemoryUsageRecorded>(recorded => recorded.WorkspaceIds.Contains(Workspace.Id) ? LoadLastUsedAsync() : Task.CompletedTask);
    }

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
        _page = 0;
        await LoadAsync();
    }

    private Task OnChangedAsync(FeatureMapsChanged changed) => changed.WorkspaceId == Workspace.Id ? LoadAsync() : Task.CompletedTask;

    private async Task LoadAsync()
    {
        try
        {
            var view = await Service.LoadAsync(Workspace.Id, _searchedFor, _page, _order, Cancel);
            if (view.Maps.Count == 0 && _page > 0 && view.Total > 0)
            {
                _page = Math.Max(0, (view.Total - 1) / FeatureMapAdminService.PageSize);
                view = await Service.LoadAsync(Workspace.Id, _searchedFor, _page, _order, Cancel);
            }

            _maps = view.Maps;
            _total = view.Total;
            _error = null;
            await LoadNotesAsync();
            await LoadLastUsedAsync();

            // Show the maps at once: the check reads and parses every file the steps name, which takes seconds when there are many maps.
            _status = NoStatus;
            _checkedUtc = null;
            _checking = true;
            _loaded = true;
            await InvokeAsync(StateHasChanged);

            // Only as many steps as the budget allows, in page order; the rest are checked when opened.
            var maps = _maps;
            var budgeted = new List<FeatureMap>();
            var steps = 0;
            foreach (var map in maps)
            {
                if (budgeted.Count > 0 && steps + map.Steps.Count > CheckBudgetSteps)
                {
                    break;
                }

                budgeted.Add(map);
                steps += map.Steps.Count;
            }

            var status = await Service.CheckAsync(Workspace, budgeted, Cancel);

            // A newer load may have replaced the maps while this one was checking: its answer is for the old steps.
            if (ReferenceEquals(maps, _maps))
            {
                _status = status;
                _checkedUtc = Time.GetUtcNow();
                _noteStaleness = await Notes.CheckAsync(Workspace, _notes, Cancel);
                RecordHealth();
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

    private async Task LoadLastUsedAsync()
    {
        try
        {
            _lastUsed = _maps.Count == 0 ? new Dictionary<long, DateTimeOffset>() : await Service.LastUsedAsync(Workspace.Id, [.. _maps.Select(map => map.Id)], Cancel);
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "The last-used times of the flows of workspace {WorkspaceId} could not be loaded", Workspace.Id);
        }
    }

    // When every flow of the workspace is on this page and was checked, the Overview may show the counts.
    private void RecordHealth()
    {
        if (_searchedFor.Length > 0 || _total > _maps.Count || _maps.Any(map => !_status.ContainsKey(map.Id)))
        {
            return;
        }

        var broken = _maps.Sum(map => BrokenCount(map) ?? 0);
        var missing = _maps.Sum(map => MissingCount(map) ?? 0);
        Health.SetLinks(Workspace.Id, broken, missing);
    }

    // A flow opened before the check reached it is checked on its own.
    private async Task OnToggledAsync(FeatureMap map, bool expanded)
    {
        if (!expanded || _checking || _status.ContainsKey(map.Id))
        {
            return;
        }

        try
        {
            var one = await Service.CheckAsync(Workspace, [map], Cancel);
            var merged = new Dictionary<long, IReadOnlyList<FeatureComponentStatus>>(_status);
            foreach (var (id, statuses) in one)
            {
                merged[id] = statuses;
            }

            _status = merged;
            RecordHealth();
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "The flow {FlowId} could not be checked", map.Id);
        }
    }

    private int StepsShownOf(FeatureMap map) => Math.Min(map.Steps.Count, _stepsShown.TryGetValue(map.Id, out var shown) ? shown : StepChunk);

    private void ShowMoreSteps(FeatureMap map) => _stepsShown[map.Id] = StepsShownOf(map) + StepChunk;

    private DateTimeOffset? LastUsedOf(FeatureMap map) => _lastUsed.TryGetValue(map.Id, out var used) ? used : null;

    private string LastUsedTitle(FeatureMap map) => LastUsedOf(map) is { } used
        ? $"An agent last opened this flow on {DisplayFormat.DateAndClock(used)}"
        : "No agent has opened this flow yet (memory_recall get)";

    private Task ChangeOrderAsync(MemoryOrder order)
    {
        _order = order;
        _page = 0;
        return RunBusyAsync(LoadAsync, "The flows could not be loaded.");
    }

    private Task GoToPageAsync(int page)
    {
        _page = Math.Clamp(page, 0, Pages - 1);
        return RunBusyAsync(LoadAsync, "The flows could not be loaded.");
    }

    private Task SearchAsync()
    {
        _searchedFor = _query.Trim();
        _page = 0;
        return RunBusyAsync(LoadAsync, "The flows could not be searched.");
    }

    private Task ShowAllAsync()
    {
        _query = string.Empty;
        _searchedFor = string.Empty;
        _page = 0;
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
            _noteStaleness = await Notes.CheckAsync(Workspace, _notes, Cancel);
            _checkedUtc = Time.GetUtcNow();
            _error = null;
            RecordHealth();
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

    // Null while the flow has not been checked.
    private int? MissingCount(FeatureMap map) =>
        _status.TryGetValue(map.Id, out var statuses) ? statuses.Count(status => !status.Available) : null;

    private int? BrokenCount(FeatureMap map) =>
        _status.TryGetValue(map.Id, out var statuses) ? statuses.Count(status => status.Link == FlowLinkState.DoesNotCall) : null;

    private static string? WhyNotRemovable(FeatureMap map, int index) => FeatureMapAdminService.WhyNotRemovable(map, index);

    // The same text memory_save takes (indented under the step that calls it), so a flow can be pasted into a note or saved again.
    private static string StepsText(FeatureMap map) => FlowStepText.Format(FlowStepText.FromStored(map.Steps));

    // The steps are paths: they wrap anywhere (see .wm-flow-step), so a long path never pushes the actions out of sight. Depth maps to a fixed set of indent classes.
    private static string IndentClass(FeatureMapStep step) => $"wm-indent-{Math.Clamp(step.Depth, 0, 8)}";

    // A step spec is 'path:Type.Member', 'path#L10-40' or 'path'. The member is split off so it is the headline and the long path the small print.
    private static (string Path, string? Member) SplitSpec(string spec)
    {
        var colon = spec.LastIndexOf(':');
        if (colon > 0 && colon < spec.Length - 1)
        {
            var tail = spec[(colon + 1)..];
            if (tail.IndexOfAny(['/', '\\']) < 0)
            {
                return (spec[..colon], tail);
            }
        }

        return (spec, null);
    }

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

    private async Task LoadNotesAsync()
    {
        try
        {
            _notes = _maps.Count == 0 ? [] : await Notes.ForFlowsAsync(Workspace.Id, [.. _maps.Select(map => map.Id)], Cancel);
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "The notes of the flows of workspace {WorkspaceId} could not be loaded", Workspace.Id);
            _error = "The notes could not be loaded.";
        }
    }

    private IReadOnlyList<Note> NotesOf(FeatureMap map) => [.. _notes.Where(note => note.MapId == map.Id)];

    private IReadOnlyList<Note> FlowNotes(FeatureMap map) => [.. _notes.Where(note => note.MapId == map.Id && note.StepSpec is null)];

    private IReadOnlyList<Note> StepNotes(FeatureMap map, string spec) =>
        [.. _notes.Where(note => note.MapId == map.Id && string.Equals(note.StepSpec, spec, StringComparison.OrdinalIgnoreCase))];

    // Notes on a step that the flow no longer has: kept, shown apart, so the owner can retire them or Claude can move what still holds.
    private IReadOnlyList<Note> OrphanNotes(FeatureMap map) =>
        [.. _notes.Where(note => note.MapId == map.Id && note.StepSpec is { } spec && !map.Steps.Any(step => string.Equals(step.Spec, spec, StringComparison.OrdinalIgnoreCase)))];

    private NoteStaleness? NoteStalenessOf(Note note) => _noteStaleness.TryGetValue(note.Id, out var found) ? found : null;

    private bool IsEditorFor(FeatureMap map, string? spec) =>
        _editorAt is { } at && at.MapId == map.Id && string.Equals(at.Spec, spec, StringComparison.OrdinalIgnoreCase);

    private string EditorTitle(FeatureMap map, string? spec) =>
        _editingNote is not null ? $"Edit note #{_editingNote.Id}"
        : spec is null ? $"New note on the flow {map.Name}"
        : $"New note on step {NoteEmbedText.Label(spec)}";

    private void StartAddNote(FeatureMap map, string? spec)
    {
        _editingNote = null;
        _noteError = null;
        _editorAt = (map.Id, spec);
    }

    private void StartEditNote(Note note)
    {
        var map = _maps.FirstOrDefault(m => m.Id == note.MapId);
        var onStep = map is not null && note.StepSpec is { } spec && map.Steps.Any(step => string.Equals(step.Spec, spec, StringComparison.OrdinalIgnoreCase));
        _editingNote = note;
        _noteError = null;
        _editorAt = note.MapId is { } mapId ? (mapId, onStep ? note.StepSpec : null) : null;
    }

    private void CloseNoteEditor()
    {
        _editorAt = null;
        _editingNote = null;
        _noteError = null;
    }

    private async Task SaveNoteAsync(FeatureMap map, string? spec, NoteEditorValue value)
    {
        _busy = true;
        try
        {
            if (_editingNote is { } editing)
            {
                await Notes.EditAsync(Workspace, editing.Id, value.Title, value.Body, value.Kind, Cancel);
                Snackbar.Add("Saved as a new revision.", Severity.Success);
            }
            else
            {
                var saved = await Notes.AddAsync(Workspace, map.Name, spec, null, value.Kind, value.Title, value.Body, Cancel);
                Snackbar.Add(saved.Similar.Count > 0 ? $"Saved. {saved.Similar.Count} similar note(s) are on the same step." : "Saved.", Severity.Success);
            }

            CloseNoteEditor();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            _noteError = ex.Message;
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "A note could not be saved");
            _noteError = "The note could not be saved.";
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task RetireNoteAsync(Note note)
    {
        var confirmed = await ConfirmDialogs.AskAsync(Dialogs, "Retire this note?", $"Note #{note.Id} is no longer shown or searched. It is kept as history.", "Retire", destructive: true);
        if (confirmed)
        {
            await NoteActionAsync(() => Notes.RetireAsync(Workspace, note.Id, Cancel), "Retired.");
        }
    }

    private Task ConfirmNoteAsync(Note note) => NoteActionAsync(() => Notes.ConfirmAsync(Workspace, note.Id, Cancel), "Confirmed against the code as it is now.");

    private Task TogglePinAsync(Note note) =>
        NoteActionAsync(() => Notes.SetPinnedAsync(Workspace, note.Id, !note.Pinned, Cancel), note.Pinned ? "Unpinned." : "Pinned: it is in the workspace guide now.");

    private Task ShowHistoryAsync(Note note) => RunBusyAsync(
        async () =>
        {
            _history = await Notes.HistoryAsync(Workspace.Id, note.Id, Cancel);
            _historyOf = note.Id;
        },
        "The history could not be loaded.");

    private Task NoteActionAsync(Func<Task<Note>> action, string done) => RunBusyAsync(
        async () =>
        {
            try
            {
                await action();
                Snackbar.Add(done, Severity.Success);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or KeyNotFoundException)
            {
                Snackbar.Add(ex.Message, Severity.Warning);
            }
        },
        "The note could not be changed.");
    private string Caption()
    {
        var count = $"{DisplayFormat.Count(_total)} {(_total == 1 ? "flow" : "flows")}";
        var matching = _searchedFor.Length > 0 ? $" match '{_searchedFor}'" : string.Empty;
        var first = (_page * FeatureMapAdminService.PageSize) + 1;
        var shown = _total > _maps.Count ? $", {first}-{first + _maps.Count - 1} shown (page {_page + 1} of {Pages})" : string.Empty;
        var notChecked = _maps.Count(map => !_status.ContainsKey(map.Id));
        var later = notChecked > 0 ? $"; {notChecked} more are checked when you open them" : string.Empty;
        var checkedText = _checking
            ? " Checking against the code..."
            : _checkedUtc is { } at
                ? $" Checked against the code {DisplayFormat.Ago(at, Time.GetUtcNow())}{later}."
                : string.Empty;
        return $"{count}{matching}{shown}.{checkedText}";
    }
}
