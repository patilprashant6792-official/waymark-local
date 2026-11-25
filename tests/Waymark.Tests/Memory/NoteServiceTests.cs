using Waymark.Core.Events;
using Waymark.Core.Memory;
using Waymark.Core.Workspaces;
using Waymark.Data.Stores;
using Waymark.Modules.Code;
using Waymark.Modules.Code.Memory;
using Waymark.Modules.Code.Reading;
using Waymark.Tests.Replace;
using Waymark.Tests.Support;

namespace Waymark.Tests.Memory;

public sealed class NoteServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);
    private static readonly NoteAuthor Owner = new(NoteAuthorKind.Human, "admin-ui");
    private static readonly NoteAuthor Claude = new(NoteAuthorKind.Agent, "claude-ai");

    private const string Code = "namespace App;\n\npublic class A\n{\n    public void Run()\n    {\n        var x = 1;\n    }\n}\n";

    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task AStepNoteIsSavedWithAFingerprintAndPublished()
    {
        await using var kit = await Kit.CreateAsync(_ct);

        var saved = await kit.Service.AddAsync(kit.Workspace, Claude, "run-flow", "src/A.cs:A.Run", null, "gotcha", "Run is called twice on start", _ct);

        Assert.Equal("src/A.cs:A.Run", saved.Note.StepSpec);
        Assert.StartsWith(StepFingerprint.Prefix, saved.Note.CodeHash, StringComparison.Ordinal);
        Assert.Equal(1, saved.Note.FlowVersion);
        Assert.Contains(kit.Events.Published, e => e is NoteChanged { Change: NoteChange.Saved });
    }

    [Fact]
    public async Task TextThatLooksLikeAKeyIsRefused()
    {
        await using var kit = await Kit.CreateAsync(_ct);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            kit.Service.AddAsync(kit.Workspace, Claude, null, null, null, null, "use AKIAABCDEFGHIJKLMNOP for the bucket", _ct));

        Assert.Contains("secret", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, await kit.Db.CountAsync("notes", _ct));
    }

    [Fact]
    public async Task ANoteOverTheCapIsRefused() =>
        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await using var kit = await Kit.CreateAsync(_ct);
            await kit.Service.AddAsync(kit.Workspace, Claude, null, null, null, null, new string('a', NoteRules.MaxBodyChars + 1), _ct);
        });

    [Fact]
    public async Task AStepThatIsNotInTheFlowIsRefused()
    {
        await using var kit = await Kit.CreateAsync(_ct);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            kit.Service.AddAsync(kit.Workspace, Claude, "run-flow", "src/A.cs:A.Nope", null, null, "x", _ct));

        Assert.Contains("not a step of flow", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnknownFlowIsRefused() =>
        await Assert.ThrowsAsync<KeyNotFoundException>(async () =>
        {
            await using var kit = await Kit.CreateAsync(_ct);
            await kit.Service.AddAsync(kit.Workspace, Claude, "no-such-flow", null, null, null, "x", _ct);
        });

    [Fact]
    public async Task AModelCannotEditOrRetireTheOwnersNote()
    {
        await using var kit = await Kit.CreateAsync(_ct);
        var owners = (await kit.Service.AddAsync(kit.Workspace, Owner, null, null, null, "convention", "Keep A small", _ct)).Note;

        var edit = await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Service.EditAsync(kit.Workspace, Claude, owners.Id, "changed", null, _ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Service.RetireAsync(kit.Workspace, Claude, owners.Id, _ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Service.PinAsync(kit.Workspace, Claude, owners.Id, true, _ct));

        Assert.Contains("written by the owner", edit.Message, StringComparison.Ordinal);
        Assert.True((await kit.Notes.GetAsync(kit.Workspace.Id, [owners.Id], _ct))[0].IsActive);
    }

    [Fact]
    public async Task TheOwnerRetiresClaudesNote()
    {
        await using var kit = await Kit.CreateAsync(_ct);
        var claudes = (await kit.Service.AddAsync(kit.Workspace, Claude, null, null, null, null, "a hint", _ct)).Note;

        var retired = await kit.Service.RetireAsync(kit.Workspace, Owner, claudes.Id, _ct);

        Assert.False(retired.IsActive);
    }

    [Fact]
    public async Task ChangingTheAnchoredCodeMakesTheNoteMayBeStaleUntilConfirmed()
    {
        await using var kit = await Kit.CreateAsync(_ct);
        var note = (await kit.Service.AddAsync(kit.Workspace, Claude, "run-flow", "src/A.cs:A.Run", null, null, "x starts at one", _ct)).Note;
        Assert.False((await kit.Service.CheckAsync(kit.Workspace, [note], _ct))[note.Id].MayBeStale);

        kit.WriteCode(Code.Replace("var x = 1;", "var x = 2;", StringComparison.Ordinal));
        var stale = (await kit.Service.CheckAsync(kit.Workspace, [note], _ct))[note.Id];

        Assert.True(stale.MayBeStale);
        Assert.Equal("code changed", stale.Reason);

        var confirmed = await kit.Service.ConfirmAsync(kit.Workspace, Claude, note.Id, _ct);
        Assert.False((await kit.Service.CheckAsync(kit.Workspace, [confirmed], _ct))[note.Id].MayBeStale);
    }

    [Fact]
    public async Task OnlyTrailingSpacesChangedIsNotStale()
    {
        await using var kit = await Kit.CreateAsync(_ct);
        var note = (await kit.Service.AddAsync(kit.Workspace, Claude, "run-flow", "src/A.cs:A.Run", null, null, "note", _ct)).Note;

        kit.WriteCode(Code.Replace("var x = 1;", "var x = 1;   ", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal));

        Assert.False((await kit.Service.CheckAsync(kit.Workspace, [note], _ct))[note.Id].MayBeStale);
    }

    [Fact]
    public async Task ANoteAboutAFileThatIsNotHereHasNoFingerprintAndNoError()
    {
        await using var kit = await Kit.CreateAsync(_ct);

        var saved = await kit.Service.AddAsync(kit.Workspace, Claude, null, null, "src/Missing.cs:M.Go", "why", "kept for the branch that has it", _ct);

        Assert.Null(saved.Note.CodeHash);
        Assert.Equal("src/Missing.cs:M.Go", saved.Note.About);
    }

    [Fact]
    public async Task OnlyTheOwnerPinsAndThePinBudgetHolds()
    {
        await using var kit = await Kit.CreateAsync(_ct);
        var ids = new List<long>();
        for (var i = 0; i < NoteRules.MaxPinned + 1; i++)
        {
            ids.Add((await kit.Service.AddAsync(kit.Workspace, Owner, null, null, null, null, $"rule {i}", _ct)).Note.Id);
        }

        for (var i = 0; i < NoteRules.MaxPinned; i++)
        {
            await kit.Service.PinAsync(kit.Workspace, Owner, ids[i], true, _ct);
        }

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Service.PinAsync(kit.Workspace, Owner, ids[^1], true, _ct));
        Assert.Contains("pin budget", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEditKeepsTheOldRevision()
    {
        await using var kit = await Kit.CreateAsync(_ct);
        var first = (await kit.Service.AddAsync(kit.Workspace, Claude, null, null, null, null, "first", _ct)).Note;

        var second = await kit.Service.EditAsync(kit.Workspace, Claude, first.Id, "second", null, _ct);

        Assert.Equal(first.Id, second.ReplacesId);
        Assert.Equal(2, (await kit.Notes.HistoryAsync(kit.Workspace.Id, second.Id, _ct)).Count);
        await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Service.EditAsync(kit.Workspace, Claude, first.Id, "third", null, _ct));
    }

    [Fact]
    public void TheStepIsMatchedAsRecallShowsIt()
    {
        var map = new FeatureMap(1, "f", "s", 1, true, string.Empty, Now, [new FeatureMapStep("src/A.cs:A.Run", 0, "call", string.Empty)]);

        Assert.Equal("src/A.cs:A.Run", NoteService.StepOf(map, "src/a.cs:A.Run"));
        Assert.Equal("src/A.cs:A.Run", NoteService.StepOf(map, "`src/A.cs:A.Run | the trigger`"));
    }

    private sealed class Kit : IAsyncDisposable
    {
        private Kit(TestDatabase db, TempWorkspace temp)
        {
            Db = db;
            Temp = temp;
            Notes = new NoteStore(db.Connections, db.Writes);
            Maps = new FeatureMapStore(db.Connections, db.Writes);
            var languages = new LanguageIndexers([new CSharpIndexer(CodeIndexOptions.FromConfiguration(new TestConfiguration()))]);
            var reader = new FlowStepReader(new FolderPathGuard(temp.Workspace), languages, CodeReadOptions.FromConfiguration(new TestConfiguration()));
            Service = new NoteService(Notes, Maps, new NoteAnchors(reader), new KeyInspector(), new EmptyMemorySearch(), Events, new ManualTimeProvider(Now));
        }

        public TestDatabase Db { get; }

        public TempWorkspace Temp { get; }

        public Workspace Workspace => Temp.Workspace;

        public NoteStore Notes { get; }

        public FeatureMapStore Maps { get; }

        public RecordingEventBus Events { get; } = new();

        public NoteService Service { get; }

        public static async Task<Kit> CreateAsync(CancellationToken ct)
        {
            var db = await TestDatabase.CreateAsync(ct);
            var temp = new TempWorkspace();
            var stored = await new WorkspaceStore(db.Connections, db.Writes).AddAsync(temp.Workspace.Name, temp.Root, Now, ct);
            Assert.Equal(temp.Workspace.Id, stored!.Id);
            var kit = new Kit(db, temp);
            kit.WriteCode(Code);
            await kit.Maps.CreateAsync(stored.Id, "run-flow", "Runs A.", string.Empty, [new FeatureMapStep("src/A.cs:A.Run", 0, "call", string.Empty)], Now, ct);
            return kit;
        }

        public void WriteCode(string text) => Temp.Write("src/A.cs", System.Text.Encoding.UTF8.GetBytes(text));

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            Temp.Dispose();
        }
    }
}
