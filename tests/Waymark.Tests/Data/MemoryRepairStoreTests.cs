using Waymark.Core.Memory;
using Waymark.Data.Stores;
using Waymark.Tests.Support;

namespace Waymark.Tests.Data;

public sealed class MemoryRepairStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 9, 0, 0, TimeSpan.Zero);

    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task AFindingIsOpenedOnceAndTheSeqRisesOnlyForNewItems()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var (store, ws) = await SetUpAsync(db);
        var scope = new[] { new RepairScope(MemoryRepairTarget.Flow, 7) };

        var first = await store.SyncAsync(ws, scope, [Gone(7, "src/A.cs:A.Run")], "test", Now, _ct);
        var again = await store.SyncAsync(ws, scope, [Gone(7, "src/A.cs:A.Run")], "test", Now.AddMinutes(1), _ct);

        Assert.Equal(1, first.Opened);
        Assert.Equal(0, again.Opened);
        Assert.Equal(first.Seq, again.Seq);
        Assert.Equal(1, again.Open);
        Assert.Single(await store.ListOpenAsync(ws, null, 0, 10, _ct));
    }

    [Fact]
    public async Task AnOpenItemInScopeThatIsNotFoundAgainIsResolved()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var (store, ws) = await SetUpAsync(db);
        var scope = new[] { new RepairScope(MemoryRepairTarget.Flow, 7) };
        await store.SyncAsync(ws, scope, [Gone(7, "src/A.cs:A.Run")], "test", Now, _ct);

        var healthy = await store.SyncAsync(ws, scope, [], "agent", Now.AddMinutes(1), _ct);

        Assert.Equal(1, healthy.Resolved);
        Assert.Equal(0, healthy.Open);
    }

    [Fact]
    public async Task ItemsOutsideTheScopeAreLeftAlone()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var (store, ws) = await SetUpAsync(db);
        await store.SyncAsync(ws, [new RepairScope(MemoryRepairTarget.Flow, 7)], [Gone(7, "src/A.cs:A.Run"), Gone(7, "src/B.cs:B.Go")], "test", Now, _ct);

        // Only step A was re-checked (a file change): B stays open.
        var partial = await store.SyncAsync(ws, [new RepairScope(MemoryRepairTarget.Flow, 7, "src/A.cs:A.Run")], [], "test", Now, _ct);

        Assert.Equal(1, partial.Resolved);
        Assert.Equal("src/B.cs:B.Go", Assert.Single(await store.ListOpenAsync(ws, null, 0, 10, _ct)).StepSpec);
    }

    [Fact]
    public async Task ATargetIsResolvedWhole()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var (store, ws) = await SetUpAsync(db);
        await store.SyncAsync(ws, [new RepairScope(MemoryRepairTarget.Flow, 7)], [Gone(7, "a.cs:A.B"), Gone(7, "b.cs:B.C")], "test", Now, _ct);

        Assert.Equal(2, await store.ResolveTargetAsync(ws, MemoryRepairTarget.Flow, 7, "deleted", Now, _ct));
        Assert.Empty(await store.ListOpenAsync(ws, null, 0, 10, _ct));
    }

    [Fact]
    public async Task CountsSplitAgentsAndOwnerAndKeepTheOtherBranchFigure()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var (store, ws) = await SetUpAsync(db);
        var ownerNote = new RepairFinding(MemoryRepairKind.NoteCodeChanged, MemoryRepairTarget.Note, 3, "Rule", -1, string.Empty, "src/A.cs", "code changed", "src/A.cs", true);
        await store.SyncAsync(ws, [new RepairScope(MemoryRepairTarget.Flow, 7), new RepairScope(MemoryRepairTarget.Note, 3)], [Gone(7, "a.cs:A.B"), ownerNote], "test", Now, _ct);
        await store.SetFullScanAsync(ws, 4, "main", Now, _ct);

        var counts = await store.CountsAsync(ws, Now.AddDays(-7), _ct);

        Assert.Equal(2, counts.Open);
        Assert.Equal(1, counts.ForAgents);
        Assert.Equal(1, counts.ForOwner);
        Assert.Equal(4, counts.OtherBranchSteps);
        Assert.Equal("main", counts.Branch);
        Assert.True(counts.Seq > 0);
        Assert.Equal(counts, (await store.CountsAllAsync(Now.AddDays(-7), _ct))[ws]);
    }

    [Fact]
    public async Task OpenItemsAreFoundByTheirFile()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var (store, ws) = await SetUpAsync(db);
        await store.SyncAsync(ws, [new RepairScope(MemoryRepairTarget.Flow, 7)], [Gone(7, "src/A.cs:A.Run")], "test", Now, _ct);

        Assert.Single(await store.ListOpenForPathAsync(ws, "src/A.cs", 5, _ct));
        Assert.Empty(await store.ListOpenForPathAsync(ws, "src/B.cs", 5, _ct));
    }

    [Fact]
    public async Task AnAutoFixResolvesTheOpenItemAndCanBeUndoneOnce()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var (store, ws) = await SetUpAsync(db);
        await store.SyncAsync(ws, [new RepairScope(MemoryRepairTarget.Flow, 7)], [Moved(7, "src/A.cs:A.Run", "src/B.cs:A.Run")], "test", Now, _ct);
        var fix = new RepairFinding(MemoryRepairKind.StepMoved, MemoryRepairTarget.Flow, 7, "flow", 0, "src/B.cs:A.Run", "src/B.cs", "moved", "src/A.cs:A.Run", false);

        var id = await store.RecordAutoFixAsync(ws, fix, new AutoFixUndo(7, "flow", 3, 0, "src/A.cs:A.Run", "src/B.cs:A.Run", 1), Now, _ct);

        Assert.Empty(await store.ListOpenAsync(ws, null, 0, 10, _ct));
        Assert.Equal(MemoryRepairState.AutoFixed, (await store.GetAsync(ws, id, _ct))!.State);
        Assert.Equal(1, (await store.CountsAsync(ws, Now.AddDays(-1), _ct)).AutoFixedRecently);
        Assert.False(await store.WasUndoneAsync(ws, 7, "src/A.cs:A.Run", "src/B.cs:A.Run", _ct));

        Assert.True(await store.MarkUndoneAsync(ws, id, Now, _ct));
        Assert.False(await store.MarkUndoneAsync(ws, id, Now, _ct));
        Assert.True(await store.WasUndoneAsync(ws, 7, "src/A.cs:A.Run", "src/B.cs:A.Run", _ct));
    }

    private static RepairFinding Gone(long flow, string spec) =>
        new(MemoryRepairKind.StepGone, MemoryRepairTarget.Flow, flow, "flow", 0, spec, spec.Split(':', '#')[0], "not found", null, false);

    private static RepairFinding Moved(long flow, string spec, string to) =>
        new(MemoryRepairKind.StepMoved, MemoryRepairTarget.Flow, flow, "flow", 0, spec, spec.Split(':', '#')[0], "not found", to, false);

    private static async Task<(MemoryRepairStore Store, long WorkspaceId)> SetUpAsync(TestDatabase db)
    {
        var workspace = await new WorkspaceStore(db.Connections, db.Writes).AddAsync("waymark", "C:/code/waymark", Now, TestContext.Current.CancellationToken);
        return (new MemoryRepairStore(db.Connections, db.Writes), workspace!.Id);
    }
}
