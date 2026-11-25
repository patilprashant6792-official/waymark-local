using Microsoft.Extensions.Logging.Abstractions;
using Waymark.Core.Events;
using Waymark.Core.Memory;
using Waymark.Core.Workspaces;
using Waymark.Data.Stores;
using Waymark.Modules.Code.Memory.Semantic;
using Waymark.Tests.Data;
using Waymark.Tests.Support;

namespace Waymark.Tests.Memory;

public sealed class MemorySearchTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);
    private static readonly NoteAuthor Claude = new(NoteAuthorKind.Agent, "claude-ai");
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task WithTheSwitchOffOnlyWordsAreUsedAndNothingIsSent()
    {
        await using var kit = await Kit.CreateAsync(_ct);
        var note = await kit.AddNoteAsync("Only one notice per call");

        var result = await kit.Search.SearchAsync(kit.Workspace.Id, "notice", 10, _ct);

        Assert.False(result.UsedMeaning);
        Assert.Null(result.Notice);
        var only = Assert.Single(result.Hits);
        Assert.Equal(note.Id, only.Id);
        Assert.Null(only.Similarity);
        Assert.Equal(0, kit.Provider.Calls);
    }

    [Fact]
    public async Task MeaningFindsANoteThatSharesNoWordWithTheQuery()
    {
        await using var kit = await Kit.CreateAsync(_ct);
        var note = await kit.AddNoteAsync("Only one notice per call");
        kit.Provider.Meanings["notice"] = MemoryEmbeddingStoreTests.Hot(5);
        kit.Provider.Meanings["reminder"] = MemoryEmbeddingStoreTests.Hot(5);
        await kit.IndexAsync();

        var result = await kit.Search.SearchAsync(kit.Workspace.Id, "single reminder", 10, _ct);

        Assert.True(result.UsedMeaning);
        var hit = Assert.Single(result.Hits, h => h.Id == note.Id);
        Assert.Equal(MemoryMatch.Meaning, hit.MatchedBy);
        Assert.NotNull(hit.Similarity);
        Assert.InRange(hit.Similarity.Value, 0.99, 1.01);
    }

    [Fact]
    public async Task AFlowTakesTheBestSimilarityOfItselfAndTheNotesGroupedUnderIt()
    {
        await using var kit = await Kit.CreateAsync(_ct);
        var map = await kit.CreateFlowAsync("guide-delivery");
        await kit.Notes.AddAsync(new NewNote(kit.Workspace.Id, map, "src/A.cs:A.Run", null, NoteKind.Gotcha, "one notice per call", Claude, null, 1), Now, _ct);
        kit.Provider.Meanings["notice"] = MemoryEmbeddingStoreTests.Hot(5);
        kit.Provider.Meanings["reminder"] = MemoryEmbeddingStoreTests.Hot(5);
        await kit.IndexAsync();

        var result = await kit.Search.SearchAsync(kit.Workspace.Id, "single reminder", 10, _ct);

        var hit = Assert.Single(result.Hits, h => h.Kind == MemoryHitKind.Flow && h.Id == map);
        Assert.NotNull(hit.Similarity);
        Assert.InRange(hit.Similarity.Value, 0.99, 1.01);
    }

    [Fact]
    public async Task AProviderFailureFallsBackToWordsAndSaysSo()
    {
        await using var kit = await Kit.CreateAsync(_ct);
        var note = await kit.AddNoteAsync("Only one notice per call");
        await kit.IndexAsync();
        kit.Provider.FailWith = new EmbeddingException(EmbeddingFailure.Transient, "down");

        var result = await kit.Search.SearchAsync(kit.Workspace.Id, "notice", 10, _ct);

        Assert.False(result.UsedMeaning);
        Assert.Equal(MemorySearch.Unavailable, result.Notice);
        Assert.Equal(note.Id, Assert.Single(result.Hits).Id);
    }

    [Fact]
    public async Task WordsAndMeaningTogetherRankAboveWordsAlone()
    {
        await using var kit = await Kit.CreateAsync(_ct);
        var both = await kit.AddNoteAsync("cache entries expire after an hour");
        var wordsOnly = await kit.AddNoteAsync("the cache folder is ignored by git");
        kit.Provider.Meanings["expire"] = MemoryEmbeddingStoreTests.Hot(7);
        kit.Provider.Meanings["lifetime"] = MemoryEmbeddingStoreTests.Hot(7);
        await kit.IndexAsync();

        var result = await kit.Search.SearchAsync(kit.Workspace.Id, "cache lifetime", 10, _ct);

        Assert.Equal(both.Id, result.Hits[0].Id);
        Assert.Equal(MemoryMatch.Both, result.Hits[0].MatchedBy);
        Assert.Contains(result.Hits, hit => hit.Id == wordsOnly.Id);
    }

    [Fact]
    public async Task AStepNoteIsGroupedUnderItsFlowWithTheStep()
    {
        await using var kit = await Kit.CreateAsync(_ct);
        var map = await kit.CreateFlowAsync("guide-delivery");
        var note = await kit.Notes.AddAsync(new NewNote(kit.Workspace.Id, map, "src/A.cs:A.Run", null, NoteKind.Gotcha, "one notice per call", Claude, null, 1), Now, _ct);

        var result = await kit.Search.SearchAsync(kit.Workspace.Id, "notice", 10, _ct);

        var hit = Assert.Single(result.Hits);
        Assert.Equal(MemoryHitKind.Flow, hit.Kind);
        Assert.Equal(map, hit.Id);
        Assert.Equal([note.Id], hit.Notes);
        Assert.Equal(["src/A.cs:A.Run"], hit.Steps);
    }

    [Fact]
    public async Task RenamingAFlowMakesItAndItsNotesPending()
    {
        await using var kit = await Kit.CreateAsync(_ct);
        var map = await kit.CreateFlowAsync("old-name");
        await kit.Notes.AddAsync(new NewNote(kit.Workspace.Id, map, "src/A.cs:A.Run", null, NoteKind.Note, "a step note", Claude, null, 1), Now, _ct);
        await kit.IndexAsync();
        Assert.Empty(await kit.Indexer.PendingAsync(kit.Workspace, kit.ModelId, _ct));

        await kit.Maps.ReplaceAsync(kit.Workspace.Id, "old-name", 1, "new-name", null, [new FeatureMapStep("src/A.cs:A.Run", 0, "call", string.Empty)], Now, _ct);

        var pending = await kit.Indexer.PendingAsync(kit.Workspace, kit.ModelId, _ct);
        Assert.Contains(pending, item => item.Kind == EmbeddingItemKind.Flow);
        Assert.Contains(pending, item => item.Kind == EmbeddingItemKind.Note);
    }

    [Fact]
    public async Task SwitchingTheModelEmbedsEverythingAgainThenDropsTheOldVectors()
    {
        await using var kit = await Kit.CreateAsync(_ct);
        await kit.AddNoteAsync("first");
        await kit.AddNoteAsync("second");
        await kit.IndexAsync();
        var oldModel = kit.ModelId;

        kit.Settings.Model = "model-b";
        await kit.IndexAsync();

        Assert.Equal(0, await kit.Vectors.CountAsync(oldModel, _ct));
        Assert.Equal(2, await kit.Vectors.CountAsync(kit.ModelId, _ct));
    }

    [Fact]
    public async Task AnExcludedWorkspaceIsNeverSentAndItsVectorsAreDeleted()
    {
        await using var kit = await Kit.CreateAsync(_ct);
        await kit.AddNoteAsync("secretless but private");
        await kit.IndexAsync();
        var sentBefore = kit.Provider.Calls;
        kit.Settings.Excluded.Add(kit.Workspace.Id);
        var status = new MemoryIndexStatus(kit.Events);
        using var sync = new MemoryEmbeddingSync(
            kit.Events, new WorkspaceStore(kit.Db.Connections, kit.Db.Writes), kit.Settings, kit.Provider, kit.Vectors, kit.Indexer, kit.Cache, kit.Queries, status, TimeProvider.System,
            NullLogger<MemoryEmbeddingSync>.Instance);

        await sync.StartAsync(_ct);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (status.Current.State != MemoryIndexState.Idle && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100, _ct);
        }

        await sync.StopAsync(_ct);
        Assert.Equal(MemoryIndexState.Idle, status.Current.State);
        Assert.Equal(sentBefore, kit.Provider.Calls);
        Assert.Equal(0, await kit.Vectors.CountAsync(kit.ModelId, _ct));
    }

    private sealed class Kit : IAsyncDisposable
    {
        private Kit(TestDatabase db, Workspace workspace)
        {
            Db = db;
            Workspace = workspace;
            Notes = new NoteStore(db.Connections, db.Writes);
            Maps = new FeatureMapStore(db.Connections, db.Writes);
            Vectors = new MemoryEmbeddingStore(db.Connections, db.Writes);
            Cache = new MemoryVectorCache(Vectors);
            Queries = new QueryEmbedder(Provider, Settings);
            Indexer = new MemoryWorkspaceIndexer(new MemoryItems(Notes, Maps), Vectors, Cache, TimeProvider.System, NullLogger<MemoryWorkspaceIndexer>.Instance);
            Search = new MemorySearch(Notes, new FlowWordCache(Maps, new EventBus(NullLogger<EventBus>.Instance)), Settings, Queries, Cache, NullLogger<MemorySearch>.Instance);
        }

        public TestDatabase Db { get; }

        public Workspace Workspace { get; }

        public NoteStore Notes { get; }

        public FeatureMapStore Maps { get; }

        public MemoryEmbeddingStore Vectors { get; }

        public MemoryVectorCache Cache { get; }

        public QueryEmbedder Queries { get; }

        public MemoryWorkspaceIndexer Indexer { get; }

        public MemorySearch Search { get; }

        public FakeEmbeddingProvider Provider { get; } = new();

        public FakeMemorySettings Settings { get; } = new();

        public EventBus Events { get; } = new(NullLogger<EventBus>.Instance);

        public string ModelId => new MemorySettings(true, Settings.Provider, Settings.Model, Settings.Dimension, true).ModelId;

        public static async Task<Kit> CreateAsync(CancellationToken ct)
        {
            var db = await TestDatabase.CreateAsync(ct);
            var workspace = await new WorkspaceStore(db.Connections, db.Writes).AddAsync("w", "C:/w", Now, ct);
            return new Kit(db, workspace!);
        }

        public Task<Note> AddNoteAsync(string body) =>
            Notes.AddAsync(new NewNote(Workspace.Id, null, null, null, NoteKind.Note, body, Claude, null, null), Now, TestContext.Current.CancellationToken);

        public async Task<long> CreateFlowAsync(string name) =>
            (await Maps.CreateAsync(Workspace.Id, name, "A flow.", string.Empty, [new FeatureMapStep("src/A.cs:A.Run", 0, "call", string.Empty)], Now, TestContext.Current.CancellationToken)).Id;

        /// <summary>Turns search by meaning on and runs one index pass over the workspace.</summary>
        public async Task IndexAsync()
        {
            Settings.Enabled = true;
            var active = await Settings.GetAsync(TestContext.Current.CancellationToken);
            await Indexer.RunAsync(Workspace, active, Provider, Settings.Key!, _ => ValueTask.CompletedTask, TestContext.Current.CancellationToken);
        }

        public async ValueTask DisposeAsync() => await Db.DisposeAsync();
    }
}
