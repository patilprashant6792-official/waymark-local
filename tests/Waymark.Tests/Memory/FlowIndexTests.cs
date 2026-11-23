using Microsoft.Extensions.Logging.Abstractions;
using Waymark.Core.Events;
using Waymark.Core.Memory;
using Waymark.Core.Workspaces;
using Waymark.Data.Stores;
using Waymark.Host.Mcp;
using Waymark.Modules.Code.Memory;
using Waymark.Tests.Support;

namespace Waymark.Tests.Memory;

/// <summary>
/// Pull-only memory: the workspace guide carries an index of flows (name and id) only with search by meaning off and none with it on,
/// recall pages the rest in the same order and size, and the search answer with search by meaning on is name, match score and id.
/// </summary>
public sealed class FlowIndexTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 9, 0, 0, TimeSpan.Zero);
    private static readonly NoteAuthor Claude = new(NoteAuthorKind.ClaudeClient, "claude-ai");
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public void AnIndexLineIsTheNameAndTheIdOnly() => Assert.Equal("guide-delivery  id 12", FlowIndex.Line(12, "guide-delivery"));

    [Fact]
    public void TheGuideAndRecallPageTheSameWay() => Assert.Equal(FlowIndex.PageSize, WorkspaceSetup.MaxMapNamesPerWorkspace);

    [Fact]
    public async Task TheStoreListsOneWorkspacesNewestNamesAndCountsAll()
    {
        await using var kit = await Kit.CreateAsync(_ct);
        await kit.CreateFlowsAsync(kit.Workspace.Id, 3);
        var other = await kit.AddWorkspaceAsync("other");
        await kit.CreateFlowsAsync(other.Id, 2);

        var index = await kit.Maps.ListNamesAsync(kit.Workspace.Id, 2, _ct);
        var counts = await kit.Maps.CountByWorkspaceAsync(_ct);

        Assert.Equal(3, index.Total);
        Assert.Equal(["flow-2", "flow-1"], index.Newest.Select(map => map.Name).ToList());
        Assert.Equal(3, counts[kit.Workspace.Id]);
        Assert.Equal(2, counts[other.Id]);
    }

    [Fact]
    public async Task AnEmptyWorkspaceHasAnEmptyIndexAndNoCount()
    {
        await using var kit = await Kit.CreateAsync(_ct);

        var index = await kit.Maps.ListNamesAsync(kit.Workspace.Id, FlowIndex.PageSize, _ct);

        Assert.Equal(0, index.Total);
        Assert.Empty(index.Newest);
        Assert.False((await kit.Maps.CountByWorkspaceAsync(_ct)).ContainsKey(kit.Workspace.Id));
    }

    [Fact]
    public async Task WithMeaningOffTheGuideListsTheFirstFiftyByNameAndIdAndPointsToPageTwo()
    {
        await using var kit = await Kit.CreateAsync(_ct);
        await kit.CreateFlowsAsync(kit.Workspace.Id, FlowIndex.PageSize + 3);

        var text = await kit.Setup().DescribeAsync(kit.Workspace, _ct);

        Assert.Contains("- Flows saved, newest first (name and id;", text, StringComparison.Ordinal);
        Assert.Contains($"  - flow-{FlowIndex.PageSize + 2}  id ", text, StringComparison.Ordinal);
        Assert.Contains("  - flow-3  id ", text, StringComparison.Ordinal);
        Assert.DoesNotContain("  - flow-2  id ", text, StringComparison.Ordinal);
        Assert.Contains("  - and 3 more: recall(action='list', page=2)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("steps", text, StringComparison.Ordinal);
        Assert.DoesNotContain("A flow.", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithMeaningOffAndNoFlowsTheGuideSaysNoneYet()
    {
        await using var kit = await Kit.CreateAsync(_ct);

        var text = await kit.Setup().DescribeAsync(kit.Workspace, _ct);

        Assert.Contains("  - none yet", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithMeaningOnTheGuideListsNoFlowsOnlyTheirCount()
    {
        await using var kit = await Kit.CreateAsync(_ct);
        await kit.CreateFlowsAsync(kit.Workspace.Id, 4);
        kit.Settings.Enabled = true;

        var text = await kit.Setup().DescribeAsync(kit.Workspace, _ct);

        Assert.Contains("- Flows: 4 saved, not listed here because search by meaning is on.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("flow-0", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Flows saved", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnExcludedWorkspaceKeepsTheIndexWhileMeaningIsOn()
    {
        await using var kit = await Kit.CreateAsync(_ct);
        await kit.CreateFlowsAsync(kit.Workspace.Id, 1);
        kit.Settings.Enabled = true;
        kit.Settings.Excluded.Add(kit.Workspace.Id);

        var setup = kit.Setup();

        Assert.False(await setup.MeaningOnAsync(kit.Workspace.Id, _ct));
        Assert.Contains("  - flow-0  id ", await setup.DescribeAsync(kit.Workspace, _ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PinnedNotesStayInTheGuideWithMeaningOn()
    {
        await using var kit = await Kit.CreateAsync(_ct);
        var note = await kit.Notes.AddAsync(new NewNote(kit.Workspace.Id, null, null, null, NoteKind.Convention, "Every call passes here", Claude, null, null), Now, _ct);
        await kit.Notes.SetPinnedAsync(kit.Workspace.Id, note.Id, true, Now, _ct);
        kit.Settings.Enabled = true;

        var text = await kit.Setup().DescribeAsync(kit.Workspace, _ct);

        Assert.Contains("- Pinned notes", text, StringComparison.Ordinal);
        Assert.Contains("Every call passes here", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RecallListPagesNameAndIdOnly()
    {
        var items = new[] { new FeatureMapSummary(7, "guide-delivery", "Attaches the guide.", 15, 2, true, Now) };

        var text = FlowRecallRenderer.List("waymark", 2, 3, 120, items);

        Assert.Contains("120 saved, newest first, page 2 of 3.", text, StringComparison.Ordinal);
        Assert.Contains("- guide-delivery  id 7", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Attaches the guide.", text, StringComparison.Ordinal);
        Assert.Contains("More: page=3.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WithMeaningOnTheSearchAnswerIsNameScoreAndId()
    {
        var map = Map(164, "recall-memory-search");
        var words = Map(121, "recall-list");
        var result = new MemorySearchResult(
        [
            new MemoryHit(MemoryHitKind.Flow, 164, 164, null, 0.05, MemoryMatch.Both, ["src/A.cs:A.Run"], [9], 0.8234),
            new MemoryHit(MemoryHitKind.Flow, 121, 121, null, 0.02, MemoryMatch.Words, ["src/A.cs:A.Run"], []),
        ], true, null);

        var text = MemorySearchRenderer.Render("waymark", "search memory", result, new Dictionary<long, FeatureMap> { [164] = map, [121] = words }, new Dictionary<long, Note>());

        Assert.Contains("1. recall-memory-search  0.82  id 164", text, StringComparison.Ordinal);
        Assert.Contains("2. recall-list  id 121", text, StringComparison.Ordinal);
        Assert.DoesNotContain("A flow summary.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("step 1", text, StringComparison.Ordinal);
        Assert.DoesNotContain("[both]", text, StringComparison.Ordinal);
        Assert.Contains("recall(action='get', ids='<id>')", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WithMeaningOnAStandaloneNoteIsOneLineWithItsScore()
    {
        var note = new Note(57, 1, null, null, null, "src/Waymark.Host/Mcp/ToolPipeline.cs", null, NoteKind.Convention, "Every call passes here", "h", Claude, null, null, null, null, null, null, null, false, Now, Now);
        var result = new MemorySearchResult([new MemoryHit(MemoryHitKind.Note, 57, null, null, 0.03, MemoryMatch.Meaning, [], [], 0.7)], true, null);

        var text = MemorySearchRenderer.Render("waymark", "pipeline", result, new Dictionary<long, FeatureMap>(), new Dictionary<long, Note> { [57] = note });

        Assert.Contains("1. note #57 convention [claude-client: claude-ai] about src/Waymark.Host/Mcp/ToolPipeline.cs: Every call passes here  0.70", text, StringComparison.Ordinal);
        Assert.Contains(NoteRules.HintLine, text, StringComparison.Ordinal);
    }

    [Fact]
    public void WithMeaningOffTheSearchAnswerHasNoScoreAndKeepsTheSteps()
    {
        var map = Map(164, "recall-memory-search");
        var result = new MemorySearchResult([new MemoryHit(MemoryHitKind.Flow, 164, 164, null, 0.05, MemoryMatch.Words, ["src/A.cs:A.Run"], [])], false, null);

        var text = MemorySearchRenderer.Render("waymark", "search", result, new Dictionary<long, FeatureMap> { [164] = map }, new Dictionary<long, Note>());

        Assert.Contains("1. flow recall-memory-search (id 164, v1)  [words]", text, StringComparison.Ordinal);
        Assert.Contains("A flow summary.", text, StringComparison.Ordinal);
        Assert.Contains("step 1", text, StringComparison.Ordinal);
        Assert.DoesNotContain("match score", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AScoreOutsideZeroToOneIsClamped()
    {
        var map = Map(1, "a");
        var result = new MemorySearchResult([new MemoryHit(MemoryHitKind.Flow, 1, 1, null, 0.05, MemoryMatch.Meaning, [], [], -0.2)], true, null);

        var text = MemorySearchRenderer.Render("waymark", "x", result, new Dictionary<long, FeatureMap> { [1] = map }, new Dictionary<long, Note>());

        Assert.Contains("1. a  0.00  id 1", text, StringComparison.Ordinal);
    }

    [Fact]
    public void MeaningCountsAsOnWhenItFailedForThisQuery()
    {
        Assert.True(new MemorySearchResult([], false, "search by meaning is unavailable right now").MeaningOn);
        Assert.True(new MemorySearchResult([], true, null).MeaningOn);
        Assert.False(new MemorySearchResult([], false, null).MeaningOn);
    }

    private static FeatureMap Map(long id, string name) =>
        new(id, name, "A flow summary.", 1, true, string.Empty, Now, [new FeatureMapStep("src/A.cs:A.Run", 0, "call", string.Empty)]);

    private sealed class Kit : IAsyncDisposable
    {
        private readonly EventBus _events = new(NullLogger<EventBus>.Instance);

        private Kit(TestDatabase db, Workspace workspace)
        {
            Db = db;
            Workspace = workspace;
            Maps = new FeatureMapStore(db.Connections, db.Writes);
            Notes = new NoteStore(db.Connections, db.Writes);
        }

        public TestDatabase Db { get; }

        public Workspace Workspace { get; }

        public FeatureMapStore Maps { get; }

        public NoteStore Notes { get; }

        public FakeMemorySettings Settings { get; } = new();

        public static async Task<Kit> CreateAsync(CancellationToken ct)
        {
            var db = await TestDatabase.CreateAsync(ct);
            var workspace = await new WorkspaceStore(db.Connections, db.Writes).AddAsync("w", "C:/w", Now, ct);
            return new Kit(db, workspace!);
        }

        public async Task<Workspace> AddWorkspaceAsync(string name) =>
            (await new WorkspaceStore(Db.Connections, Db.Writes).AddAsync(name, "C:/" + name, Now, TestContext.Current.CancellationToken))!;

        // flow-0 is the oldest; each next one is a minute newer.
        public async Task CreateFlowsAsync(long workspaceId, int count)
        {
            for (var i = 0; i < count; i++)
            {
                await Maps.CreateAsync(
                    workspaceId,
                    $"flow-{i}",
                    "A flow.",
                    string.Empty,
                    [new FeatureMapStep($"src/A{i}.cs:A{i}.Run", 0, "call", string.Empty)],
                    Now.AddMinutes(i),
                    TestContext.Current.CancellationToken);
            }
        }

        // A fresh setup per call: it remembers the search-by-meaning switch for a while.
        public WorkspaceSetup Setup() =>
            new(Maps, new CommandPolicyStore(Db.Connections, Db.Writes), new PinnedNotes(Notes, _events), Notes, Settings, TimeProvider.System);

        public async ValueTask DisposeAsync() => await Db.DisposeAsync();
    }
}
