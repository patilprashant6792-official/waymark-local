using Waymark.Core.Memory;
using Waymark.Data.Stores;
using Waymark.Tests.Support;

namespace Waymark.Tests.Data;

public sealed class FeatureMapBranchTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 9, 0, 0, TimeSpan.Zero);
    private static readonly NoteAuthor Claude = new(NoteAuthorKind.Agent, "claude-ai");

    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task AStepKeepsItsBranchWhenTheFlowIsReplacedWithoutOne()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var (maps, ws) = await SetUpAsync(db);
        var created = await maps.CreateAsync(ws, "f", "s", string.Empty, [Step("src/A.cs:A.Run", "feature/x"), Step("src/B.cs:B.Go", "main", 1)], Now, _ct);

        await maps.ReplaceAsync(ws, "f", 1, null, null, [Step("src/A.cs:A.Run", null), Step("src/B.cs:B.Go", "release", 1)], Now, _ct);

        var steps = (await maps.GetAsync(ws, [created.Id], _ct))[0].Steps;
        Assert.Equal("feature/x", steps[0].Branch);
        Assert.Equal("release", steps[1].Branch);
    }

    [Fact]
    public async Task StampingRecordsTheBranchWithoutANewVersion()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var (maps, ws) = await SetUpAsync(db);
        var created = await maps.CreateAsync(ws, "f", "s", string.Empty, [Step("src/A.cs:A.Run", null)], Now, _ct);

        Assert.Equal(1, await maps.StampBranchAsync(ws, created.Id, ["src/A.cs:A.Run"], "main", _ct));
        Assert.Equal(0, await maps.StampBranchAsync(ws, created.Id, ["src/A.cs:A.Run"], "main", _ct));

        var map = (await maps.GetAsync(ws, [created.Id], _ct))[0];
        Assert.Equal("main", map.Steps[0].Branch);
        Assert.Equal(1, map.Version);
    }

    [Fact]
    public async Task StampingIgnoresAnotherWorkspacesFlow()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var (maps, ws) = await SetUpAsync(db);
        var created = await maps.CreateAsync(ws, "f", "s", string.Empty, [Step("src/A.cs:A.Run", null)], Now, _ct);

        Assert.Equal(0, await maps.StampBranchAsync(ws + 1, created.Id, ["src/A.cs:A.Run"], "main", _ct));
    }

    [Fact]
    public async Task StepNotesFollowAMovedStep()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var (maps, ws) = await SetUpAsync(db);
        var created = await maps.CreateAsync(ws, "f", "s", string.Empty, [Step("src/A.cs:A.Run", null)], Now, _ct);
        var notes = new NoteStore(db.Connections, db.Writes);
        var note = await notes.AddAsync(new NewNote(ws, created.Id, "src/A.cs:A.Run", null, NoteKind.Gotcha, "runs twice", Claude, null, 1, "Runs twice"), Now, _ct);

        Assert.Equal(1, await notes.RebaseStepAsync(ws, created.Id, "src/A.cs:A.Run", "src/B.cs:A.Run", _ct));

        var moved = (await notes.GetAsync(ws, [note.Id], _ct))[0];
        Assert.Equal("src/B.cs:A.Run", moved.StepSpec);
        Assert.True(moved.IsActive);
        Assert.Equal(note.Id, Assert.Single(await notes.FindActiveByPathAsync(ws, "src/B.cs", 10, _ct)));
    }

    [Fact]
    public async Task ListIdsGivesEveryFlowNewestFirst()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var (maps, ws) = await SetUpAsync(db);
        var older = await maps.CreateAsync(ws, "older", "s", string.Empty, [Step("a.cs:A.B", null)], Now, _ct);
        var newer = await maps.CreateAsync(ws, "newer", "s", string.Empty, [Step("b.cs:B.C", null)], Now.AddMinutes(1), _ct);

        Assert.Equal([newer.Id, older.Id], await maps.ListIdsAsync(ws, 10, _ct));
    }

    private static FeatureMapStep Step(string spec, string? branch, int depth = 0) => new(spec, depth, "call", string.Empty, branch);

    private static async Task<(FeatureMapStore Maps, long WorkspaceId)> SetUpAsync(TestDatabase db)
    {
        var workspace = await new WorkspaceStore(db.Connections, db.Writes).AddAsync("waymark", "C:/code/waymark", Now, TestContext.Current.CancellationToken);
        return (new FeatureMapStore(db.Connections, db.Writes), workspace!.Id);
    }
}
