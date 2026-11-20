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
    FromClaude,
    MayBeStale,
    Detached,
    Retired,
}

/// <summary>
/// The Notes section of a workspace: lists the notes by filter, searches them, adds and edits the owner's own, retires, confirms and pins any, imports drafts
/// from a pasted CLAUDE.md, and excludes the workspace from search by meaning. It reloads when a note changes.
/// </summary>
public partial class NoteList : LiveComponentBase
{
    private static readonly IReadOnlyList<WmOption<NotesView>> ViewOptions =
    [
        new(NotesView.All, "All"),
        new(NotesView.Pinned, "Pinned"),
        new(NotesView.FromClaude, "From Claude"),
        new(NotesView.MayBeStale, "May be stale"),
        new(NotesView.Detached, "Detached"),
        new(NotesView.Retired, "Retired"),
    ];

    private static readonly IReadOnlyList<WmOption<NoteKind>> KindOptions = [.. NoteKinds.All.Select(kind => new WmOption<NoteKind>(kind, NoteKinds.ToText(kind)))];

    private NotesView _view = NotesView.All;
    private IReadOnlyList<Note> _notes = [];
    private IReadOnlyList<Note> _pinned = [];
    private IReadOnlyDictionary<long, NoteStaleness> _staleness = new Dictionary<long, NoteStaleness>();
    private int _total;
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

    [Inject]
    private NoteAdminService Service { get; set; } = default!;

    [Inject]
    private MemoryAdminService Memory { get; set; } = default!;

    [Inject]
    private IDialogService Dialogs { get; set; } = default!;

    [Inject]
    private ISnackbar Snackbar { get; set; } = default!;

    [Inject]
    private ILogger<NoteList> Logger { get; set; } = default!;

    private IReadOnlyList<Note> Shown => _view == NotesView.MayBeStale
        ? [.. _notes.Where(note => StalenessOf(note) is { MayBeStale: true })]
        : _notes;

    protected override void OnInitialized() => RefreshOn<NoteChanged>(changed => changed.WorkspaceId == Workspace.Id ? LoadAsync() : Task.CompletedTask);

    protected override async Task OnParametersSetAsync()
    {
        if (_shownId == Workspace.Id)
        {
            return;
        }

        _shownId = Workspace.Id;
        _search = null;
        _loaded = false;
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            var page = await Service.ListAsync(Workspace.Id, FilterOf(_view), 0, Cancel);
            _notes = page.Items;
            _total = page.Total;
            _pinned = await Service.PinnedAsync(Workspace.Id, Cancel);
            _excluded = await Memory.IsExcludedAsync(Workspace.Id, Cancel);
            _staleness = await Service.CheckAsync(Workspace, [.. _notes.Concat(_searchNotes.Select(item => item.Note))], Cancel);
            if (_search is not null)
            {
                await RunSearchAsync();
            }

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

    private Task ChangeViewAsync(NotesView view)
    {
        _view = view;
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
        NotesView.FromClaude => NoteFilter.FromModels,
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

        var shown = Shown.Count;
        var more = _total > _notes.Count ? $" The newest {_notes.Count} are listed." : string.Empty;
        return $"{shown} note(s) shown, {_total} in this view.{more}";
    }

    /// <summary>One draft of an import, editable before it is saved.</summary>
    private sealed class DraftRow
    {
        public string Body { get; set; } = string.Empty;

        public NoteKind Kind { get; set; }

        public bool Keep { get; set; }
    }
}
