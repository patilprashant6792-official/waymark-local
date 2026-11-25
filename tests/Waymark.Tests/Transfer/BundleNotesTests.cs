using Waymark.Core.Memory;
using Waymark.Core.Transfer;
using Waymark.Data.Stores;
using Waymark.Tests.Support;

namespace Waymark.Tests.Transfer;

public sealed class BundleNotesTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public void TheSanitizerKeepsGoodNotesAndLeavesOutBadOnes()
    {
        var bundle = Bundle(
            Note("a good   note"),
            Note(string.Empty),
            Note("key AKIAABCDEFGHIJKLMNOP here"),
            Note("x") with { Kind = "rule" },
            Note("x") with { AuthorKind = "robot" },
            Note("step without a flow") with { Step = "a.cs:A.B" },
            Note("about a range") with { About = "a.cs#L1-2" });

        var clean = WorkspaceBundleSanitizer.Sanitize(bundle);

        var kept = Assert.Single(Assert.Single(clean.Bundle.Workspaces).Notes!);
        Assert.Equal("a good note", kept.Body);
        Assert.Contains(clean.Notes, note => note.Contains("6 note(s) were left out", StringComparison.Ordinal));
    }

    [Fact]
    public void ANoteThatIsHereAlreadyIsNotAddedTwice()
    {
        var here = new Note(1, 1, null, null, null, "src/A.cs", null, NoteKind.Note, "Same", "same text", NoteRules.Hash("same text"),
            new NoteAuthor(NoteAuthorKind.Human, "admin-ui"), null, null, null, null, null, null, null, false, At, At);
        var incoming = new BundleWorkspace("app", null, true, [], [], null, [Note("same text") with { About = "src/A.cs" }, Note("new text")]);

        var plan = BundleMergePlanner.Plan(incoming, BundleContent.Notes, [], [], null, [here]);

        Assert.Equal(1, plan.NotesNew);
        Assert.Equal(1, plan.NotesAlreadyThere);
        Assert.Equal("new text", plan.NotesToAdd![0].Body);
        Assert.True(plan.HasChanges);
    }

    [Fact]
    public void NotesAreLeftAloneWhenTheContentDoesNotAskForThem()
    {
        var incoming = new BundleWorkspace("app", null, true, [], [], null, [Note("new text")]);

        Assert.Equal(0, BundleMergePlanner.Plan(incoming, BundleContent.FeatureMaps, [], []).NotesNew);
    }

    [Fact]
    public async Task TheStoreAddsNotesWithProvenanceAndDetachesThoseWithoutTheirFlow()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var workspace = await new WorkspaceStore(db.Connections, db.Writes).AddAsync("app", "C:/app", At, _ct);
        var plan = BundleMergePlanner.Plan(
            new BundleWorkspace("app", null, true, [], [], null, [Note("plain"), Note("on a flow") with { Flow = "missing-flow", Step = "a.cs:A.B" }]),
            BundleContent.Notes,
            [],
            []);

        var applied = await new WorkspaceBundleStore(db.Writes).ApplyAsync([new WorkspaceApply(workspace!.Id, plan)], At, _ct);

        Assert.Equal(2, Assert.Single(applied).NotesAdded);
        var notes = await new NoteStore(db.Connections, db.Writes).ListActiveAsync(workspace.Id, _ct);
        Assert.All(notes, note => Assert.Equal(new NoteAuthor(NoteAuthorKind.Agent, "claude-ai"), note.Author));
        Assert.Equal("missing-flow", notes.Single(note => note.Body == "on a flow").DetachedFrom);
        Assert.Equal(2, await db.CountAsync("note_fts", _ct));
    }

    [Fact]
    public void NotesSurviveAWriteAndARead()
    {
        var text = WorkspaceBundleJson.Serialize(Bundle(Note("kept")));

        Assert.True(WorkspaceBundleJson.TryParse(text, out var bundle, out var error), error);
        Assert.Equal("kept", Assert.Single(Assert.Single(bundle.Workspaces).Notes!).Body);
    }

    private static BundleNote Note(string body) => new(body, "note", null, null, null, null, "claude-client", "claude-ai", At, At);

    private static WorkspaceBundle Bundle(params BundleNote[] notes) =>
        new(WorkspaceBundleJson.FormatName, WorkspaceBundleJson.CurrentVersion, At, [new BundleWorkspace("app", null, true, [], [], null, notes)]);
}
