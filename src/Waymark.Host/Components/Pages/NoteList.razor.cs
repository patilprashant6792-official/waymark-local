using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using MudBlazor;
using Waymark.Core.Events;
using Waymark.Core.Memory;
using Waymark.Core.Workspaces;
using Waymark.Host.Components.Shared;
using Waymark.Host.Components.Ui;
using Waymark.Host.Memory;
using Waymark.Host.Notes;

namespace Waymark.Host.Components.Pages;

/// <summary>Which notes the Notes page lists.</summary>
public enum NotesView
{
    Pinned,
    All,
    FromAgents,
    MayBeStale,
    Detached,
    Retired,
}

/// <summary>
/// The Notes section of a workspace: lists the notes a page at a time by filter and order, searches them, adds notes and edits any (an agent's note edited
/// here becomes yours), retires, confirms and pins any, imports drafts from a pasted CLAUDE.md, and excludes the workspace from search by meaning.
/// It reloads when a note changes and refreshes the last-used times when agents used notes.
/// </summary>
public partial class NoteList : LiveComponentBase
{
    /// <summary>The most notes the 'May be stale' view checks against the code in one load.</summary>
    internal const int MaxStaleScan = 1000;

    private static readonly IReadOnlyList<WmOption<NotesView>> ViewOptions =
    [
        new(NotesView.All, "All"),
        new(NotesView.Pinned, "Pinned"),
        new(NotesView.FromAgents, "From agents"),
        new(NotesView.MayBeStale, "May be stale"),
        new(NotesView.Detached, "Detached"),
        new(NotesView.Retired, "Retired"),
    ];

    private static readonly IReadOnlyList<WmOption<MemoryOrder>> OrderOptions =
    [
        new(MemoryOrder.Changed, "Last changed"),
        new(MemoryOrder.Used, "Last used"),
    ];

    private static readonly IReadOnlyList<WmOption<NoteKind>> KindOptions = [.. NoteKinds.All.Select(kind => new WmOption<NoteKind>(kind, NoteKinds.ToText(kind)))];

    private NotesView _view = NotesView.All;
    private MemoryOrder _order = MemoryOrder.Changed;
    private int _page;
    private IReadOnlyList<Note> _notes = [];
    private IReadOnlyList<Note> _pinned = [];
    private IReadOnlyDictionary<long, NoteStaleness> _staleness = new Dictionary<long, NoteStaleness>();
    private IReadOnlyDictionary<long, DateTimeOffset> _lastUsed = new Dictionary<long, DateTimeOffset>();
    private int _total;
    private bool _staleScanCut;
    private string _query = string.Empty;
    private MemorySearchResult? _search;
    private List<(Note Note, MemoryMatch Match)> _searchNotes = [];
    private Note? _editing;
    private bool _editorOpen;
    private string? _editorError;
    private bool _importing;
    private string _importText = string.Empty;
    private List<DraftRow> _drafts = [];
    private long? _historyOf;
    private IReadOnlyList<Note> _history = [];
    private bool _excluded;
    private string? _error;
    private long _shownId;
    private bool _loaded;
    private bool _busy;

    [Parameter, EditorRequired]
    public Workspace Workspace { get; set; } = default!;

    /// <summary>The view to open with, from the address (?view=pinned|agents|stale|detached|retired), so the Overview can link to one.</summary>
    [Parameter]
    public string? InitialView { get; set; }

    [Inject]
    private NoteAdminService Service { get; set; } = default!;

    [Inject]
    private MemoryAdminService Memory { get; set; } = default!;

    [Inject]
    private MemoryHealthCache Health { get; set; } = default!;

    [Inject]
    private IDialogService Dialogs { get; set; } = default!;

    [Inject]
    private ISnackbar Snackbar { get; set; } = default!;

    [Inject]
    private ILogger<NoteList> Logger { get; set; } = default!;

    private IReadOnlyList<Note> Shown => _notes;

    private int Pages => Math.Max(1, (_total + NoteAdminService.PageSize - 1) / NoteAdminService.PageSize);

    private bool Paged => _search is null && _view != NotesView.MayBeStale && _total > NoteAdminService.PageSize;

    protected override void OnInitialized()
    {
        RefreshOn<NoteChanged>(changed => changed.WorkspaceId == Workspace.Id ? LoadAsync() : Task.CompletedTask);
        RefreshOn<MemoryUsageRecorded>(recorded => recorded.WorkspaceIds.Contains(Workspace.Id) ? LoadLastUsedAsync() : Task.CompletedTask);
    }

    protected override async Task OnParametersSetAsync()
    {
        if (_shownId == Workspace.Id)
        {
            return;
        }

        _shownId = Workspace.Id;
        _search = null;
        _loaded = false;
        _page = 0;
        _view = ViewOf(InitialView);
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            if (_view == NotesView.MayBeStale)
            {
                await LoadStaleAsync();
            }
            else
            {
                var page = await Service.ListAsync(Workspace.Id, FilterOf(_view), _page, Cancel, _order);
                if (page.Items.Count == 0 && _page > 0)
                {
                    _page = Math.Max(0, (page.Total - 1) / NoteAdminService.PageSize);
                    page = await Service.ListAsync(Workspace.Id, FilterOf(_view), _page, Cancel, _order);
                }

                _notes = page.Items;
                _total = page.Total;
                _staleness = await Service.CheckAsync(Workspace, [.. _notes.Concat(_searchNotes.Select(item => item.Note))], Cancel);
            }

            _pinned = await Service.PinnedAsync(Workspace.Id, Cancel);
            _excluded = await Memory.IsExcludedAsync(Workspace.Id, Cancel);
            if (_search is not null)
            {
                await RunSearchAsync();
            }

            await LoadLastUsedAsync();
            _error = null;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "The notes of workspace {WorkspaceId} could not be loaded", Workspace.Id);
            _error = "The notes could not be loaded.";
        }

        _loaded = true;
    }

    // 'May be stale' needs every anchored note checked against the code: page through the active notes (at most MaxStaleScan) and keep the stale ones.
    // When every active note was checked, the count goes to the Overview through the health cache.
    private async Task LoadStaleAsync()
    {
        var stale = new List<Note>();
        var staleness = new Dictionary<long, NoteStaleness>();
        var checkedCount = 0;
        var total = 0;
        for (var page = 0; checkedCount < MaxStaleScan; page++)
        {
            var batch = await Service.ListAsync(Workspace.Id, NoteFilter.All, page, Cancel);
            total = batch.Total;
            if (batch.Items.Count == 0)
            {
                break;
            }

            var judged = await Service.CheckAsync(Workspace, batch.Items, Cancel);
            foreach (var note in batch.Items)
            {
                if (judged.TryGetValue(note.Id, out var verdict) && verdict.MayBeStale)
                {
                    stale.Add(note);
                    staleness[note.Id] = verdict;
                }
            }

            checkedCount += batch.Items.Count;
            if (checkedCount >= batch.Total)
            {
                break;
            }
        }

        _notes = stale;
        _total = stale.Count;
        _staleness = staleness;
        _staleScanCut = checkedCount < total;
        if (!_staleScanCut)
        {
            Health.SetStale(Workspace.Id, stale.Count);
        }
    }

    private async Task LoadLastUsedAsync()
    {
        try
        {
            var ids = _notes.Select(note => note.Id).Concat(_searchNotes.Select(item => item.Note.Id)).Distinct().ToList();
            _lastUsed = ids.Count == 0 ? new Dictionary<long, DateTimeOffset>() : await Service.LastUsedAsync(Workspace.Id, ids, Cancel);
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "The last-used times of the notes of workspace {WorkspaceId} could not be loaded", Workspace.Id);
        }
    }

    private Task ChangeOrderAsync(MemoryOrder order)
    {
        _order = order;
        _page = 0;
        return RunBusyAsync(LoadAsync, "The notes could not be loaded.");
    }

    private Task GoToPageAsync(int page)
    {
        _page = Math.Clamp(page, 0, Pages - 1);
        return RunBusyAsync(LoadAsync, "The notes could not be loaded.");
    }

    private DateTimeOffset? LastUsedOf(Note note) => _lastUsed.TryGetValue(note.Id, out var used) ? used : null;

    private static NotesView ViewOf(string? view) => (view ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "pinned" => NotesView.Pinned,
        "agents" => NotesView.FromAgents,
        "stale" => NotesView.MayBeStale,
        "detached" => NotesView.Detached,
        "retired" => NotesView.Retired,
        _ => NotesView.All,
    };

    private Task ChangeViewAsync(NotesView view)
    {
        _view = view;
        _page = 0;
        return RunBusyAsync(LoadAsync, "The notes could not be loaded.");
    }

    private async Task OnSearchKey(KeyboardEventArgs args)
    {
        if (args.Key == "Enter" && !string.IsNullOrWhiteSpace(_query) && !_busy)
        {
            await SearchAsync();
        }
    }

    private Task SearchAsync() => RunBusyAsync(RunSearchAsync, "The notes could not be searched.");

    private async Task RunSearchAsync()
    {
        var result = await Service.SearchAsync(Workspace.Id, _query.Trim(), Cancel);
        var ids = result.Hits
            .SelectMany<MemoryHit, (long Id, MemoryMatch Match)>(hit => hit.Kind == MemoryHitKind.Note ? [(hit.Id, hit.MatchedBy)] : hit.Notes.Select(id => (id, hit.MatchedBy)))
            .DistinctBy(item => item.Id)
            .ToList();
        var found = await Service.GetAsync(Workspace.Id, [.. ids.Select(item => item.Id)], Cancel);
        var byId = found.Where(note => note.IsActive).ToDictionary(note => note.Id);
        _searchNotes = [.. ids.Where(item => byId.ContainsKey(item.Id)).Select(item => (byId[item.Id], item.Match))];
        _staleness = await Service.CheckAsync(Workspace, [.. _searchNotes.Select(item => item.Note)], Cancel);
        _search = result;
        await LoadLastUsedAsync();
    }

    private void ClearSearch()
    {
        _search = null;
        _searchNotes = [];
        _query = string.Empty;
    }

    private void StartAdd()
    {
        _editing = null;
        _editorError = null;
        _editorOpen = true;
    }

    private void StartEdit(Note note)
    {
        _editing = note;
        _editorError = null;
        _editorOpen = true;
    }

    private void CloseEditor()
    {
        _editorOpen = false;
        _editing = null;
        _editorError = null;
    }

    private async Task SaveAsync(NoteEditorValue value)
    {
        _busy = true;
        try
        {
            if (_editing is null)
            {
                var saved = await Service.AddAsync(Workspace, null, null, value.About, value.Kind, value.Body, Cancel);
                Snackbar.Add(saved.Similar.Count > 0 ? $"Saved. {saved.Similar.Count} similar note(s) exist on the same anchor." : "Saved.", Severity.Success);
            }
            else
            {
                await Service.EditAsync(Workspace, _editing.Id, value.Body, value.Kind, Cancel);
                Snackbar.Add("Saved as a new revision.", Severity.Success);
            }

            CloseEditor();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            // A refusal the owner can act on (a secret, too long, a changed note), shown in the editor.
            _editorError = ex.Message;
        }
        catch (OperationCanceledException)
        {
            // The page was closed.
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "A note could not be saved");
            _editorError = "The note could not be saved.";
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task RetireAsync(Note note)
    {
        var confirmed = await ConfirmDialogs.AskAsync(
            Dialogs,
            "Retire this note?",
            $"Note #{note.Id} is no longer shown or searched. It is kept as history.",
            "Retire",
            destructive: true);
        if (confirmed)
        {
            await ActAsync(() => Service.RetireAsync(Workspace, note.Id, Cancel), "Retired.");
        }
    }

    private Task ConfirmAsync(Note note) => ActAsync(() => Service.ConfirmAsync(Workspace, note.Id, Cancel), "Confirmed against the code as it is now.");

    private Task TogglePinAsync(Note note) =>
        ActAsync(() => Service.SetPinnedAsync(Workspace, note.Id, !note.Pinned, Cancel), note.Pinned ? "Unpinned." : "Pinned: it is in the workspace guide now.");

    private Task ShowHistoryAsync(Note note) => RunBusyAsync(
        async () =>
        {
            _history = await Service.HistoryAsync(Workspace.Id, note.Id, Cancel);
            _historyOf = note.Id;
        },
        "The history could not be loaded.");

    private Task SetExcludedAsync(bool excluded) => RunBusyAsync(
        async () =>
        {
            await Memory.SetExcludedAsync(Workspace, excluded, Cancel);
            _excluded = excluded;
            Snackbar.Add(excluded ? "Excluded: nothing of this workspace is sent, and its stored vectors are deleted." : "Included in search by meaning again.", Severity.Success);
        },
        "The setting could not be saved.");

    private void SplitImport()
    {
        _drafts = [.. InstructionSplitter.Split(_importText).Select(split => new DraftRow { Body = split.Body, Kind = split.Kind, Keep = true })];
        if (_drafts.Count == 0)
        {
            Snackbar.Add("No text to make notes from.", Severity.Info);
        }
    }

    private void CloseImport()
    {
        _importing = false;
        _importText = string.Empty;
        _drafts = [];
    }

    private Task SaveImportAsync() => RunBusyAsync(
        async () =>
        {
            var kept = _drafts.Where(draft => draft.Keep).Select(draft => new NoteDraft(draft.Body, draft.Kind, null)).ToList();
            var result = await Service.ImportAsync(Workspace, kept, Cancel);
            Snackbar.Add($"Saved {result.Saved} note(s).", Severity.Success);
            if (result.Problems.Count > 0)
            {
                _error = $"{result.Problems.Count} draft(s) were not saved: {string.Join(" ", result.Problems.Take(5))}";
            }
            else
            {
                CloseImport();
            }
        },
        "The drafts could not be saved.");

    // A refusal (another author's note, the pin budget, a changed note) is shown as a warning; anything else is a failure.
    private Task ActAsync(Func<Task<Note>> action, string done) => RunBusyAsync(
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

    private NoteStaleness? StalenessOf(Note note) => _staleness.TryGetValue(note.Id, out var found) ? found : null;

    private static NoteFilter FilterOf(NotesView view) => view switch
    {
        NotesView.Pinned => NoteFilter.Pinned,
        NotesView.FromAgents => NoteFilter.FromModels,
        NotesView.Detached => NoteFilter.Detached,
        NotesView.Retired => NoteFilter.Retired,
        _ => NoteFilter.All,
    };

    private static string MatchText(MemoryMatch match) => match switch
    {
        MemoryMatch.Both => "words + meaning",
        MemoryMatch.Meaning => "meaning",
        _ => "words",
    };

    private string Caption()
    {
        if (_search is not null)
        {
            return $"{_searchNotes.Count} note(s) found for '{_query.Trim()}'.";
        }

        if (_view == NotesView.MayBeStale)
        {
            var cut = _staleScanCut ? $" Only the newest {MaxStaleScan} notes were checked." : string.Empty;
            return $"{_total} note(s) may be stale.{cut}";
        }

        if (_total <= _notes.Count)
        {
            return $"{_total} note(s) in this view.";
        }

        var first = (_page * NoteAdminService.PageSize) + 1;
        return $"Notes {first}-{first + _notes.Count - 1} of {_total}, page {_page + 1} of {Pages}.";
    }

    /// <summary>One draft of an import, editable before it is saved.</summary>
    private sealed class DraftRow
    {
        public string Body { get; set; } = string.Empty;

        public NoteKind Kind { get; set; }

        public bool Keep { get; set; }
    }
}
