using Microsoft.Extensions.Logging.Abstractions;
using Waymark.Core.Events;
using Waymark.Core.Memory;
using Waymark.Data.Stores;
using Waymark.Host.Memory;
using Waymark.Modules.Code.Memory.Repairs;
using Waymark.Tests.Support;

namespace Waymark.Tests.Memory;

public sealed class MemoryRepairUndoTests
{
    private const string OldSpec = "src/A.cs:A.Run";
    private const string NewSpec = "src/B.cs:A.Run";

    private static readonly DateTimeOffset Now = new(2026, 11, 26, 9, 0, 0, TimeSpan.Zero);
    private static readonly NoteAuthor Claude = new(NoteAuthorKind.Agent, "claude-ai");

    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task UndoPutsTheStepBackOnItsBranchSoItsDriftIsQueuedAgain()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var setup = await SetUpAsync(db);

        await setup.Admin.UndoAsync(setup.Workspace, setup.RepairId, _ct);

        var map = (await setup.Maps.GetAsync(setup.Workspace.Id, [setup.FlowId], _ct))[0];
        Assert.Equal(OldSpec, map.Steps[0].Spec);
        Assert.Equal("main", map.Steps[0].Branch);
        Assert.Equal(StepPlace.Gone, MemoryRepairText.Judge(true, false, map.Steps[0].Branch, new(true, "main")));
        Assert.Equal(MemoryRepairState.Undone, (await setup.Repairs.GetAsync(setup.Workspace.Id, setup.RepairId, _ct))!.State);
    }

    [Fact]
    public async Task UndoMovesStepNotesBackAndKeepsCurrentFlowNotesCurrent()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var setup = await SetUpAsync(db);

        await setup.Admin.UndoAsync(setup.Workspace, setup.RepairId, _ct);

        var notes = await setup.Notes.GetAsync(setup.Workspace.Id, [setup.StepNoteId, setup.FlowNoteId], _ct);
        Assert.Equal(OldSpec, notes.Single(note => note.Id == setup.StepNoteId).StepSpec);
        Assert.Equal(2, notes.Single(note => note.Id == setup.FlowNoteId).FlowVersion);
    }

    [Fact]
    public async Task UndoIsRefusedForAnotherFlowThatGotTheSameId()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var setup = await SetUpAsync(db, flowNameInUndo: "deleted-flow");

        await Assert.ThrowsAsync<InvalidOperationException>(() => setup.Admin.UndoAsync(setup.Workspace, setup.RepairId, _ct));
    }

    [Fact]
    public async Task OnlyFlowNotesCurrentAtTheOldVersionAreMoved()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var setup = await SetUpAsync(db);
        var stale = await setup.Notes.AddAsync(new NewNote(setup.Workspace.Id, setup.FlowId, null, null, NoteKind.Why, "older", Claude, null, 0, "Older"), Now, _ct);

        Assert.Equal(1, await setup.Notes.RebaseFlowVersionAsync(setup.Workspace.Id, setup.FlowId, 1, 2, _ct));

        Assert.Equal(0, (await setup.Notes.GetAsync(setup.Workspace.Id, [stale.Id], _ct))[0].FlowVersion);
    }

    [Fact]
    public void ChangedCodeMustBeReadAgainBeforeANoteOnItIsConfirmed()
    {
        var ledger = new MemoryRepairLedger(new StepClock(Now));
        ledger.RecordRead("c", "Waymark", "src/A.cs");
        ledger.RecordRead("c", "Waymark", "src/B.cs");
        ledger.RecordRead("c", "Other", "src/A.cs");

        ledger.ForgetReads("waymark", ["src/A.cs"]);

        Assert.False(ledger.HasRead("c", "Waymark", "src/A.cs"));
        Assert.True(ledger.HasRead("c", "Waymark", "src/B.cs"));
        Assert.True(ledger.HasRead("c", "Other", "src/A.cs"));

        ledger.ForgetReads("Waymark", null);

        Assert.False(ledger.HasRead("c", "Waymark", "src/B.cs"));
    }

    private async Task<Setup> SetUpAsync(TestDatabase db, string flowNameInUndo = "f")
    {
        var workspace = (await new WorkspaceStore(db.Connections, db.Writes).AddAsync("waymark", "C:/code/waymark", Now, _ct))!;
        var maps = new FeatureMapStore(db.Connections, db.Writes);
        var notes = new NoteStore(db.Connections, db.Writes);
        var repairs = new MemoryRepairStore(db.Connections, db.Writes);

        // The state right after an automatic fix: v1 has the step at its new place, seen on main.
        var flow = await maps.CreateAsync(workspace.Id, "f", "s", string.Empty, [new FeatureMapStep(NewSpec, 0, "call", string.Empty, "main")], Now, _ct);
        var stepNote = await notes.AddAsync(new NewNote(workspace.Id, flow.Id, NewSpec, null, NoteKind.Gotcha, "runs twice", Claude, null, 1, "Runs twice"), Now, _ct);
        var flowNote = await notes.AddAsync(new NewNote(workspace.Id, flow.Id, null, null, NoteKind.Why, "why", Claude, null, 1, "Why"), Now, _ct);
        var fix = new RepairFinding(MemoryRepairKind.StepMoved, MemoryRepairTarget.Flow, flow.Id, "f", 0, NewSpec, "src/B.cs", "moved", OldSpec, false);
        var repairId = await repairs.RecordAutoFixAsync(workspace.Id, fix, new AutoFixUndo(flow.Id, flowNameInUndo, 1, 0, OldSpec, NewSpec, 1), Now, _ct);

        var admin = new MemoryRepairAdminService(repairs, new FixedSettings(), maps, notes, new EventBus(NullLogger<EventBus>.Instance), new FakeAuditStore(), new StepClock(Now));
        return new Setup(admin, workspace, maps, notes, repairs, flow.Id, stepNote.Id, flowNote.Id, repairId);
    }

    private sealed record Setup(
        MemoryRepairAdminService Admin,
        Waymark.Core.Workspaces.Workspace Workspace,
        FeatureMapStore Maps,
        NoteStore Notes,
        MemoryRepairStore Repairs,
        long FlowId,
        long StepNoteId,
        long FlowNoteId,
        long RepairId);

    private sealed class FixedSettings : IMemoryRepairSettings
    {
        public Task<MemoryRepairOptions> GetAsync(CancellationToken ct) => Task.FromResult(MemoryRepairOptions.Default);

        public Task SetAsync(MemoryRepairOptions options, string changedBy, CancellationToken ct) => Task.CompletedTask;
    }
}
