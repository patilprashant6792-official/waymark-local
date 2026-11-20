using Waymark.Core.Events;
using Waymark.Core.Memory;
using Waymark.Core.Security;
using Waymark.Core.Workspaces;

namespace Waymark.Modules.Code.Memory;

/// <summary>
/// The one write path for notes (memorise for Claude, the admin UI for the owner). It checks the text (one line, the cap, no secrets), the anchor
/// (the flow exists; a step is one of its steps, as recall shows it; about is a file or member), the writer rules, takes the code fingerprint of the anchor,
/// stores, and publishes <see cref="NoteChanged"/>. Expected refusals are ArgumentException, KeyNotFoundException or InvalidOperationException with a fix in the message.
/// </summary>
internal sealed class NoteService(
    INoteStore notes,
    IFeatureMapStore maps,
    NoteAnchors anchors,
    IContentInspector contentInspector,
    IMemorySearch search,
    IEventBus events,
    TimeProvider time) : INoteService
{
    private const int SimilarShown = 3;
    private const int SimilarSearched = 20;

    public async Task<NoteSaved> AddAsync(Workspace workspace, NoteAuthor author, string? flow, string? step, string? about, string? kind, string? body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(author);
        var text = NoteRules.Clean(body);
        var noteKind = KindOf(kind);
        var hasFlow = !string.IsNullOrWhiteSpace(flow);
        var cleanAbout = string.IsNullOrWhiteSpace(about) ? null : NoteAnchors.CleanAbout(about);
        if (!hasFlow && !string.IsNullOrWhiteSpace(step))
        {
            throw new ArgumentException("step needs name: the flow the step belongs to. Read the flow with recall(action='get') and copy the step's text.");
        }

        if (hasFlow && cleanAbout is not null)
        {
            throw new ArgumentException("about is for workspace notes (no name). A note on a flow or a step is anchored by name and step instead.");
        }

        await RefuseSecretsAsync(text + "\n" + (cleanAbout ?? string.Empty), ct).ConfigureAwait(false);

        FeatureMap? map = null;
        string? stepSpec = null;
        if (hasFlow)
        {
            var name = FlowArgs.CleanName(flow);
            map = await maps.GetByNameAsync(workspace.Id, name, ct).ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"No flow named '{name}' in workspace '{workspace.Name}'. recall(action='list') shows the names; leave name out for a workspace note.");
            stepSpec = string.IsNullOrWhiteSpace(step) ? null : StepOf(map, step);
        }

        var files = new Dictionary<string, FileView>(StringComparer.OrdinalIgnoreCase);
        var codeHash = await anchors.HashAsync(workspace, stepSpec ?? cleanAbout, files, ct).ConfigureAwait(false);
        var saved = await notes.AddAsync(
            new NewNote(workspace.Id, map?.Id, stepSpec, cleanAbout, noteKind, text, author, codeHash, map?.Version),
            time.GetUtcNow(),
            ct).ConfigureAwait(false);
        await events.PublishAsync(new NoteChanged(workspace.Id, saved.Id, NoteChange.Saved)).ConfigureAwait(false);
        return new NoteSaved(saved, await SimilarAsync(workspace.Id, saved, ct).ConfigureAwait(false));
    }

    public async Task<Note> EditAsync(Workspace workspace, NoteAuthor author, long id, string? body, string? kind, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(author);
        var current = await FindAsync(workspace, id, ct).ConfigureAwait(false);
        RequireActive(current);
        if (!NoteRules.CanRevise(author, current))
        {
            throw new InvalidOperationException(NotYours(current, "edit"));
        }

        var text = body is null ? current.Body : NoteRules.Clean(body);
        var noteKind = kind is null ? current.Kind : KindOf(kind);
        await RefuseSecretsAsync(text, ct).ConfigureAwait(false);
        var (codeHash, flowVersion) = await BaselineAsync(workspace, current, ct).ConfigureAwait(false);
        var result = await notes.ReviseAsync(workspace.Id, id, author, text, noteKind, codeHash, flowVersion, time.GetUtcNow(), ct).ConfigureAwait(false);
        var saved = Outcome(result, current, "edit");
        await events.PublishAsync(new NoteChanged(workspace.Id, saved.Id, NoteChange.Saved)).ConfigureAwait(false);
        return saved;
    }

    public async Task<Note> RetireAsync(Workspace workspace, NoteAuthor author, long id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(author);
        var current = await FindAsync(workspace, id, ct).ConfigureAwait(false);
        RequireActive(current);
        if (!NoteRules.CanRetire(author, current))
        {
            throw new InvalidOperationException(NotYours(current, "retire"));
        }

        var result = await notes.RetireAsync(workspace.Id, id, author, time.GetUtcNow(), ct).ConfigureAwait(false);
        var saved = Outcome(result, current, "retire");
        await events.PublishAsync(new NoteChanged(workspace.Id, saved.Id, NoteChange.Retired)).ConfigureAwait(false);
        return saved;
    }

    public async Task<Note> ConfirmAsync(Workspace workspace, NoteAuthor author, long id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(author);
        var current = await FindAsync(workspace, id, ct).ConfigureAwait(false);
        RequireActive(current);
        if (!NoteRules.CanConfirm(author, current))
        {
            throw new InvalidOperationException(NotYours(current, "confirm"));
        }

        var (codeHash, flowVersion) = await BaselineAsync(workspace, current, ct).ConfigureAwait(false);
        var result = await notes.ConfirmAsync(workspace.Id, id, author, codeHash, flowVersion, time.GetUtcNow(), ct).ConfigureAwait(false);
        var saved = Outcome(result, current, "confirm");
        await events.PublishAsync(new NoteChanged(workspace.Id, saved.Id, NoteChange.Saved)).ConfigureAwait(false);
        return saved;
    }

    public async Task<Note> PinAsync(Workspace workspace, NoteAuthor author, long id, bool pinned, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(author);
        if (!NoteRules.CanPin(author))
        {
            throw new InvalidOperationException("Only the owner pins notes, on the Notes page of the workspace.");
        }

        var current = await FindAsync(workspace, id, ct).ConfigureAwait(false);
        RequireActive(current);
        if (pinned && !current.Pinned)
        {
            var already = await notes.ListPinnedAsync(workspace.Id, ct).ConfigureAwait(false);
            if (!NoteRules.PinFits(already, current))
            {
                throw new InvalidOperationException(
                    $"The pin budget is {NoteRules.MaxPinned} notes and {NoteRules.MaxPinnedChars} characters per workspace; {already.Count} notes with {already.Sum(n => n.Body.Length)} characters are pinned. Unpin one first.");
            }
        }

        var result = await notes.SetPinnedAsync(workspace.Id, id, pinned, time.GetUtcNow(), ct).ConfigureAwait(false);
        var saved = Outcome(result, current, pinned ? "pin" : "unpin");
        await events.PublishAsync(new NoteChanged(workspace.Id, saved.Id, NoteChange.Saved)).ConfigureAwait(false);
        return saved;
    }

    public async Task<IReadOnlyDictionary<long, NoteStaleness>> CheckAsync(Workspace workspace, IReadOnlyList<Note> notesToCheck, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(notesToCheck);
        var result = new Dictionary<long, NoteStaleness>();
        var files = new Dictionary<string, FileView>(StringComparer.OrdinalIgnoreCase);
        var flowIds = notesToCheck.Where(n => n.IsActive && n.StepSpec is null && n.MapId is not null && n.FlowVersion is not null).Select(n => n.MapId!.Value).Distinct().ToList();
        var versions = flowIds.Count == 0
            ? new Dictionary<long, int>()
            : (await maps.GetAsync(workspace.Id, flowIds, ct).ConfigureAwait(false)).ToDictionary(map => map.Id, map => map.Version);
        foreach (var note in notesToCheck.Where(n => n.IsActive))
        {
            if (NoteAnchors.CodeSpecOf(note) is { } spec && note.CodeHash is not null && !note.IsDetached)
            {
                var now = await anchors.HashAsync(workspace, spec, files, ct).ConfigureAwait(false);
                var stale = now is not null && !string.Equals(now, note.CodeHash, StringComparison.Ordinal);
                result[note.Id] = new NoteStaleness(note.Id, stale, stale ? "code changed" : null);
            }
            else if (note.StepSpec is null && note.MapId is { } mapId && note.FlowVersion is { } written && versions.TryGetValue(mapId, out var version))
            {
                var stale = version != written;
                result[note.Id] = new NoteStaleness(note.Id, stale, stale ? "flow changed" : null);
            }
        }

        return result;
    }

    /// <summary>The stored text of the step the agent named: the exact text recall shows, or the same component written another way.</summary>
    internal static string StepOf(FeatureMap map, string step)
    {
        var wanted = step.Trim().Trim('`');
        var bar = wanted.IndexOf('|', StringComparison.Ordinal);
        if (bar >= 0)
        {
            wanted = wanted[..bar].Trim();
        }

        var canonical = FeatureComponentSpec.TryParse(wanted, out var component, out _) ? FeatureComponentSpec.Format(component) : wanted;
        var match = map.Steps.FirstOrDefault(s => string.Equals(s.Spec, wanted, StringComparison.OrdinalIgnoreCase))
            ?? map.Steps.FirstOrDefault(s => string.Equals(s.Spec, canonical, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            var some = string.Join(", ", map.Steps.Take(4).Select(s => $"'{s.Spec}'"));
            throw new ArgumentException(
                $"'{wanted}' is not a step of flow '{map.Name}'. Copy the step's text from recall(action='get', ids='{map.Id}'), for example {some}. For a note on the whole flow, leave step out.");
        }

        return match.Spec;
    }

    private static NoteKind KindOf(string? kind) =>
        NoteKinds.TryParse(kind, out var parsed)
            ? parsed
            : throw new ArgumentException($"kind is one of: {string.Join(", ", NoteKinds.All.Select(NoteKinds.ToText))}.");

    private async Task RefuseSecretsAsync(string text, CancellationToken ct)
    {
        var findings = await contentInspector.InspectAsync(text, ct).ConfigureAwait(false);
        if (findings.Count > 0)
        {
            throw new ArgumentException("The note looks like it holds a secret or a redaction marker, so nothing was saved. Take it out and call again; refer to a secret by name, never by value.");
        }
    }

    private async Task<Note> FindAsync(Workspace workspace, long id, CancellationToken ct)
    {
        var found = await notes.GetAsync(workspace.Id, [id], ct).ConfigureAwait(false);
        return found.Count > 0
            ? found[0]
            : throw new KeyNotFoundException($"No note #{id} in workspace '{workspace.Name}'. recall(action='notes', ids='workspace') lists the workspace's notes.");
    }

    private static void RequireActive(Note note)
    {
        if (!note.IsActive)
        {
            throw new InvalidOperationException(
                $"Note #{note.Id} is {(note.RetiredUtc is not null ? "retired" : "replaced by a newer revision")}. recall(action='notes', history='{note.Id}') shows its revisions; change the newest one.");
        }
    }

    private static string NotYours(Note note, string verb) =>
        $"Note #{note.Id} was written by {note.Author.Label}. A model may {verb} only its own notes, and only the owner may change a human's note. Add a new note with what you found instead, or ask the owner.";

    private static Note Outcome(NoteWriteResult result, Note current, string verb) => result.Outcome switch
    {
        NoteWrite.Saved when result.Note is not null => result.Note,
        NoteWrite.NotFound => throw new KeyNotFoundException($"Note #{current.Id} is gone."),
        NoteWrite.NotYours => throw new InvalidOperationException(NotYours(current, verb)),
        _ => throw new InvalidOperationException($"Note #{current.Id} changed while you worked on it (it is no longer the newest revision). Read it again and retry."),
    };

    /// <summary>The fingerprint of the note's code now and the version of its flow now.</summary>
    private async Task<(string? CodeHash, int? FlowVersion)> BaselineAsync(Workspace workspace, Note note, CancellationToken ct)
    {
        var files = new Dictionary<string, FileView>(StringComparer.OrdinalIgnoreCase);
        var codeHash = note.IsDetached ? note.CodeHash : await anchors.HashAsync(workspace, NoteAnchors.CodeSpecOf(note), files, ct).ConfigureAwait(false);
        int? flowVersion = note.FlowVersion;
        if (note.MapId is { } mapId)
        {
            var found = await maps.GetAsync(workspace.Id, [mapId], ct).ConfigureAwait(false);
            flowVersion = found.Count > 0 ? found[0].Version : flowVersion;
        }

        return (codeHash, flowVersion);
    }

    /// <summary>Active notes on the same anchor that the search finds for the new note's text: a hint, never a refusal.</summary>
    private async Task<IReadOnlyList<Note>> SimilarAsync(long workspaceId, Note saved, CancellationToken ct)
    {
        try
        {
            var found = await search.SearchAsync(workspaceId, saved.Body, SimilarSearched, ct).ConfigureAwait(false);
            var ids = found.Hits
                .SelectMany<MemoryHit, long>(hit => hit.Kind == MemoryHitKind.Note ? new[] { hit.Id } : hit.Notes)
                .Where(id => id != saved.Id)
                .Distinct()
                .ToList();
            if (ids.Count == 0)
            {
                return [];
            }

            var candidates = await notes.GetAsync(workspaceId, ids, ct).ConfigureAwait(false);
            return [.. candidates.Where(note => note.IsActive && SameAnchor(note, saved)).Take(SimilarShown)];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The duplicate hint is a courtesy: a failing search never fails the save.
            return [];
        }
    }

    private static bool SameAnchor(Note a, Note b) =>
        a.MapId == b.MapId
        && string.Equals(a.StepSpec, b.StepSpec, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.About, b.About, StringComparison.OrdinalIgnoreCase);
}
