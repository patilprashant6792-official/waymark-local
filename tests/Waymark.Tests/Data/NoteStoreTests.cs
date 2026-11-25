using Waymark.Core.Memory;
using Waymark.Data.Stores;
using Waymark.Tests.Support;

namespace Waymark.Tests.Data;

public sealed class NoteStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);
    private static readonly NoteAuthor Owner = new(NoteAuthorKind.Human, "admin-ui");
    private static readonly NoteAuthor Claude = new(NoteAuthorKind.Agent, "claude-ai");
    private static readonly NoteAuthor OtherClaude = new(NoteAuthorKind.Agent, "claude-code");

    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task AddsAnActiveNoteWithItsProvenance()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var (store, workspaceId, _) = await SetUpAsync(db);

        var note = await store.AddAsync(new NewNote(workspaceId, null, null, "src/A.cs", NoteKind.Gotcha, "Watch the cache", Claude, "v1:abc", null), Now, _ct);

        Assert.True(note.IsActive);
        Assert.Equal(NoteKind.Gotcha, note.Kind);
        Assert.Equal(Claude, note.Author);
        Assert.Equal("src/A.cs", note.About);
        Assert.Equal(NoteRules.Hash("Watch the cache"), note.ContentHash);
        Assert.Equal(Now, note.CreatedUtc);
        Assert.Equal(1, await db.CountAsync("note_fts", _ct));
    }

    [Fact]
    public async Task ARevisionKeepsTheOldRowAndMovesTheSearchRow()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var (store, workspaceId, mapId) = await SetUpAsync(db);
        var first = await store.AddAsync(new NewNote(workspaceId, mapId, null, null, NoteKind.Note, "alpha words", Claude, null, 1), Now, _ct);

        var revised = await store.ReviseAsync(workspaceId, first.Id, Claude, "bravo words", NoteKind.Why, null, 1, Now.AddMinutes(1), _ct);

        Assert.Equal(NoteWrite.Saved, revised.Outcome);
        Assert.Equal(first.Id, revised.Note!.ReplacesId);
        Assert.Equal(NoteKind.Why, revised.Note.Kind);
        Assert.Equal(mapId, revised.Note.MapId);
        var old = (await store.GetAsync(workspaceId, [first.Id], _ct))[0];
        Assert.False(old.IsActive);
        Assert.NotNull(old.SupersededUtc);
        Assert.Empty(await store.SearchWordsAsync(workspaceId, "\"alpha\"*", 10, _ct));
        Assert.Equal(revised.Note.Id, Assert.Single(await store.SearchWordsAsync(workspaceId, "\"bravo\"*", 10, _ct)).NoteId);
    }

    [Fact]
    public async Task AnAgentNeverChangesTheOwnersNoteButAnyAgentChangesAnAgentNote()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var (store, workspaceId, _) = await SetUpAsync(db);
        var owners = await store.AddAsync(new NewNote(workspaceId, null, null, null, NoteKind.Convention, "owner rule", Owner, null, null), Now, _ct);
        var claudes = await store.AddAsync(new NewNote(workspaceId, null, null, null, NoteKind.Note, "claude note", Claude, null, null), Now, _ct);

        Assert.Equal(NoteWrite.NotYours, (await store.ReviseAsync(workspaceId, owners.Id, Claude, "changed", NoteKind.Note, null, null, Now, _ct)).Outcome);
        Assert.Equal(NoteWrite.NotYours, (await store.RetireAsync(workspaceId, owners.Id, Claude, Now, _ct)).Outcome);
        Assert.Equal(NoteWrite.NotYours, (await store.ConfirmAsync(workspaceId, owners.Id, Claude, "v1:x", null, Now, _ct)).Outcome);

        // Another client is the same author, Agent: a re-paired client can correct an older note. The revision records the client that wrote it.
        var revised = await store.ReviseAsync(workspaceId, claudes.Id, OtherClaude, "corrected", NoteKind.Note, null, null, Now.AddMinutes(1), _ct);
        Assert.Equal(NoteWrite.Saved, revised.Outcome);
        Assert.Equal(OtherClaude, revised.Note!.Author);

        // The owner may edit an agent's note; the new revision is the owner's, and an agent can no longer change it.
        var ownersEdit = await store.ReviseAsync(workspaceId, revised.Note.Id, Owner, "owner's words", NoteKind.Note, null, null, Now.AddMinutes(2), _ct);
        Assert.Equal(NoteWrite.Saved, ownersEdit.Outcome);
        Assert.Equal(Owner, ownersEdit.Note!.Author);
        Assert.Equal(NoteWrite.NotYours, (await store.RetireAsync(workspaceId, ownersEdit.Note.Id, Claude, Now.AddMinutes(3), _ct)).Outcome);

        var retired = await store.RetireAsync(workspaceId, ownersEdit.Note.Id, Owner, Now.AddMinutes(4), _ct);
        Assert.Equal(NoteWrite.Saved, retired.Outcome);
        Assert.Equal("owner", retired.Note!.RetiredBy);
        Assert.False(retired.Note.IsActive);
        Assert.Equal(1, await db.CountAsync("note_fts", _ct));
    }

    [Fact]
    public async Task ARetiredOrMissingNoteIsReported()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var (store, workspaceId, _) = await SetUpAsync(db);
        var note = await store.AddAsync(new NewNote(workspaceId, null, null, null, NoteKind.Note, "to retire", Claude, null, null), Now, _ct);
        await store.RetireAsync(workspaceId, note.Id, Claude, Now, _ct);

        Assert.Equal(NoteWrite.NotActive, (await store.RetireAsync(workspaceId, note.Id, Claude, Now, _ct)).Outcome);
        Assert.Equal(NoteWrite.NotActive, (await store.ReviseAsync(workspaceId, note.Id, Claude, "x", NoteKind.Note, null, null, Now, _ct)).Outcome);
        Assert.Equal(NoteWrite.NotFound, (await store.RetireAsync(workspaceId, 999, Owner, Now, _ct)).Outcome);
        Assert.Equal(NoteWrite.NotFound, (await store.RetireAsync(workspaceId + 1, note.Id, Owner, Now, _ct)).Outcome);
    }

    [Fact]
    public async Task ConfirmStoresTheNewBaseline()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var (store, workspaceId, _) = await SetUpAsync(db);
        var note = await store.AddAsync(new NewNote(workspaceId, null, null, "src/A.cs", NoteKind.Note, "n", Claude, "v1:old", null), Now, _ct);

        var confirmed = await store.ConfirmAsync(workspaceId, note.Id, Claude, "v1:new", null, Now.AddHours(1), _ct);

        Assert.Equal("v1:new", confirmed.Note!.CodeHash);
        Assert.Equal(Now.AddHours(1), confirmed.Note.ConfirmedUtc);
    }

    [Fact]
    public async Task HistoryWalksTheChainBothWays()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var (store, workspaceId, _) = await SetUpAsync(db);
        var v1 = await store.AddAsync(new NewNote(workspaceId, null, null, null, NoteKind.Note, "one", Claude, null, null), Now, _ct);
        var v2 = (await store.ReviseAsync(workspaceId, v1.Id, Claude, "two", NoteKind.Note, null, null, Now.AddMinutes(1), _ct)).Note!;
        var v3 = (await store.ReviseAsync(workspaceId, v2.Id, Claude, "three", NoteKind.Note, null, null, Now.AddMinutes(2), _ct)).Note!;

        var fromMiddle = await store.HistoryAsync(workspaceId, v2.Id, _ct);

        Assert.Equal([v1.Id, v2.Id, v3.Id], fromMiddle.Select(note => note.Id));
        Assert.Equal([v1.Id, v2.Id, v3.Id], (await store.HistoryAsync(workspaceId, v1.Id, _ct)).Select(note => note.Id));
    }

    [Fact]
    public async Task PinsMoveWithARevisionAndGoAwayWhenRetired()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var (store, workspaceId, _) = await SetUpAsync(db);
        var note = await store.AddAsync(new NewNote(workspaceId, null, null, null, NoteKind.Convention, "pinned rule", Owner, null, null), Now, _ct);
        await store.SetPinnedAsync(workspaceId, note.Id, true, Now, _ct);

        var revised = (await store.ReviseAsync(workspaceId, note.Id, Owner, "pinned rule v2", NoteKind.Convention, null, null, Now, _ct)).Note!;

        Assert.True(revised.Pinned);
        Assert.Equal(revised.Id, Assert.Single(await store.ListPinnedAsync(workspaceId, _ct)).Id);
        await store.RetireAsync(workspaceId, revised.Id, Owner, Now, _ct);
        Assert.Empty(await store.ListPinnedAsync(workspaceId, _ct));
    }

    [Fact]
    public async Task DeletingAFlowDetachesItsNotes()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var (store, workspaceId, mapId) = await SetUpAsync(db);
        var maps = new FeatureMapStore(db.Connections, db.Writes);
        var note = await store.AddAsync(new NewNote(workspaceId, mapId, "src/A.cs:A.Run", null, NoteKind.Gotcha, "step note", Claude, null, 1), Now, _ct);
        Assert.Equal("guide-delivery", note.FlowName);

        await maps.DeleteAsync(workspaceId, "guide-delivery", _ct);

        var after = (await store.GetAsync(workspaceId, [note.Id], _ct))[0];
        Assert.Null(after.MapId);
        Assert.Equal("guide-delivery", after.DetachedFrom);
        Assert.True(after.IsDetached);
        Assert.True(after.IsActive);
        Assert.Single((await store.ListWorkspaceAsync(workspaceId, NoteFilter.Detached, 0, 10, _ct)).Items);
    }

    [Fact]
    public async Task RemovingTheWorkspaceRemovesItsNotesAndSearchRows()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var (store, workspaceId, mapId) = await SetUpAsync(db);
        await store.AddAsync(new NewNote(workspaceId, mapId, null, null, NoteKind.Note, "flow note", Claude, null, 1), Now, _ct);
        var v1 = await store.AddAsync(new NewNote(workspaceId, null, null, null, NoteKind.Note, "ws note", Claude, null, null), Now, _ct);
        await store.ReviseAsync(workspaceId, v1.Id, Claude, "ws note v2", NoteKind.Note, null, null, Now, _ct);

        await new WorkspaceStore(db.Connections, db.Writes).RemoveAsync(workspaceId, _ct);

        Assert.Equal(0, await db.CountAsync("notes", _ct));
        Assert.Equal(0, await db.CountAsync("note_fts", _ct));
    }

    [Fact]
    public async Task WordsFindTheBodyAndTheAnchor()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var (store, workspaceId, mapId) = await SetUpAsync(db);
        var step = await store.AddAsync(new NewNote(workspaceId, mapId, "src/A.cs:Delivery.Attach", null, NoteKind.Gotcha, "Only one notice per call", Claude, null, 1), Now, _ct);

        Assert.Equal(step.Id, Assert.Single(await store.SearchWordsAsync(workspaceId, "\"notice\"*", 10, _ct)).NoteId);
        Assert.Equal(step.Id, Assert.Single(await store.SearchWordsAsync(workspaceId, "\"delivery\"*", 10, _ct)).NoteId);
        Assert.Equal(step.Id, Assert.Single(await store.SearchWordsAsync(workspaceId, "\"attach\"*", 10, _ct)).NoteId);
        Assert.Empty(await store.SearchWordsAsync(workspaceId + 1, "\"notice\"*", 10, _ct));
    }

    [Fact]
    public async Task NotesAreFoundByTheFileTheyAreAbout()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var (store, workspaceId, mapId) = await SetUpAsync(db);
        var about = await store.AddAsync(new NewNote(workspaceId, null, null, "src/A.cs:A.Run", NoteKind.Note, "about member", Claude, null, null), Now, _ct);
        var step = await store.AddAsync(new NewNote(workspaceId, mapId, "src/A.cs:A.Run", null, NoteKind.Note, "step", Claude, null, 1), Now, _ct);
        await store.AddAsync(new NewNote(workspaceId, null, null, "src/AB.cs", NoteKind.Note, "other file", Claude, null, null), Now, _ct);

        var found = await store.FindActiveByPathAsync(workspaceId, "src/A.cs", 10, _ct);

        Assert.Equal([step.Id, about.Id], found);
    }

    private static async Task<(NoteStore Store, long WorkspaceId, long MapId)> SetUpAsync(TestDatabase db)
    {
        var ct = TestContext.Current.CancellationToken;
        var workspace = await new WorkspaceStore(db.Connections, db.Writes).AddAsync("waymark", "C:/code/waymark", Now, ct);
        var map = await new FeatureMapStore(db.Connections, db.Writes).CreateAsync(
            workspace!.Id,
            "guide-delivery",
            "Attaches the guide.",
            string.Empty,
            [new FeatureMapStep("src/A.cs:Delivery.Attach", 0, "call", string.Empty), new FeatureMapStep("src/A.cs:A.Run", 1, "call", string.Empty)],
            Now,
            ct);
        return (new NoteStore(db.Connections, db.Writes), workspace.Id, map.Id);
    }
}
