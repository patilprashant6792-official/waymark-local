using Waymark.Core.Settings;
using Waymark.Data.Stores;
using Waymark.Tests.Support;

namespace Waymark.Tests.Data;

public sealed class ConfigStoreTests
{
    private static readonly DateTimeOffset Start = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);

    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private static ConfigStore CreateStore(TestDatabase db, StepClock clock) => new(db.Connections, db.Writes, clock);

    [Fact]
    public async Task AMissingSettingIsNull()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = CreateStore(db, new StepClock(Start));

        Assert.Null(await store.GetAsync(SettingScope.Global, "missing", _ct));
    }

    [Fact]
    public async Task TheFirstSetCreatesVersionOne()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = CreateStore(db, new StepClock(Start));

        var saved = await store.SetAsync(SettingScope.Global, "limit", "10", "owner", _ct);

        Assert.Equal(1, saved.Version);
        Assert.Equal("10", saved.ValueJson);
        Assert.Equal("limit", saved.Key);
        Assert.Equal(SettingScope.Global, saved.Scope);
        Assert.Equal(Start, saved.UpdatedUtc);
        Assert.Equal(saved, await store.GetAsync(SettingScope.Global, "limit", _ct));
    }

    [Fact]
    public async Task ChangingTheValueBumpsTheVersionAndTheTime()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var clock = new StepClock(Start);
        var store = CreateStore(db, clock);
        await store.SetAsync(SettingScope.Global, "limit", "10", "owner", _ct);
        clock.Advance(TimeSpan.FromMinutes(5));

        var saved = await store.SetAsync(SettingScope.Global, "limit", "20", "owner", _ct);

        Assert.Equal(2, saved.Version);
        Assert.Equal("20", saved.ValueJson);
        Assert.Equal(Start.AddMinutes(5), saved.UpdatedUtc);
        Assert.Equal(saved, await store.GetAsync(SettingScope.Global, "limit", _ct));
    }

    [Fact]
    public async Task SettingTheSameValueAgainChangesNothing()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var clock = new StepClock(Start);
        var store = CreateStore(db, clock);
        var first = await store.SetAsync(SettingScope.Global, "limit", "10", "owner", _ct);
        clock.Advance(TimeSpan.FromHours(1));

        var again = await store.SetAsync(SettingScope.Global, "limit", "10", "someone-else", _ct);

        Assert.Equal(first, again);
        Assert.Single(await store.GetHistoryAsync(SettingScope.Global, "limit", 10, _ct));
    }

    [Fact]
    public async Task HistoryListsNewestFirstWithTheOldAndNewValues()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var clock = new StepClock(Start);
        var store = CreateStore(db, clock);
        await store.SetAsync(SettingScope.Global, "limit", "1", "alice", _ct);
        clock.Advance(TimeSpan.FromMinutes(1));
        await store.SetAsync(SettingScope.Global, "limit", "2", "bob", _ct);
        clock.Advance(TimeSpan.FromMinutes(1));
        await store.SetAsync(SettingScope.Global, "limit", "3", "carol", _ct);

        var history = await store.GetHistoryAsync(SettingScope.Global, "limit", 10, _ct);

        Assert.Equal(3, history.Count);
        Assert.Equal("3", history[0].NewJson);
        Assert.Equal("2", history[0].OldJson);
        Assert.Equal("carol", history[0].ChangedBy);
        Assert.Equal(Start.AddMinutes(2), history[0].ChangedUtc);
        Assert.Equal("2", history[1].NewJson);
        Assert.Equal("1", history[1].OldJson);
        Assert.Equal("1", history[2].NewJson);
        Assert.Null(history[2].OldJson);
        Assert.Equal("alice", history[2].ChangedBy);
    }

    [Fact]
    public async Task HistoryHonoursTheLimit()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = CreateStore(db, new StepClock(Start));
        await store.SetAsync(SettingScope.Global, "limit", "1", "o", _ct);
        await store.SetAsync(SettingScope.Global, "limit", "2", "o", _ct);
        await store.SetAsync(SettingScope.Global, "limit", "3", "o", _ct);

        var history = await store.GetHistoryAsync(SettingScope.Global, "limit", 2, _ct);

        Assert.Equal(["3", "2"], history.Select(h => h.NewJson).ToList());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(501)]
    public async Task HistoryLimitsOutsideOneToFiveHundredAreRefused(int limit)
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = CreateStore(db, new StepClock(Start));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => store.GetHistoryAsync(SettingScope.Global, "limit", limit, _ct));
    }

    [Fact]
    public async Task ScopesDoNotSeeEachOther()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = CreateStore(db, new StepClock(Start));
        var workspaceOne = new SettingScope(SettingScopeKind.Workspace, "1");
        var workspaceTwo = new SettingScope(SettingScopeKind.Workspace, "2");
        var project = new SettingScope(SettingScopeKind.Project, "1");

        await store.SetAsync(SettingScope.Global, "limit", "1", "o", _ct);
        await store.SetAsync(workspaceOne, "limit", "2", "o", _ct);
        await store.SetAsync(workspaceTwo, "limit", "3", "o", _ct);
        await store.SetAsync(project, "limit", "4", "o", _ct);

        Assert.Equal("1", (await store.GetAsync(SettingScope.Global, "limit", _ct))!.ValueJson);
        Assert.Equal("2", (await store.GetAsync(workspaceOne, "limit", _ct))!.ValueJson);
        Assert.Equal("3", (await store.GetAsync(workspaceTwo, "limit", _ct))!.ValueJson);
        Assert.Equal("4", (await store.GetAsync(project, "limit", _ct))!.ValueJson);
        Assert.Single(await store.GetHistoryAsync(workspaceOne, "limit", 10, _ct));
    }

    [Fact]
    public async Task StructuredJsonIsStoredAsGiven()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = CreateStore(db, new StepClock(Start));

        await store.SetAsync(SettingScope.Global, "shape", "{ \"a\": [1, 2] }", "o", _ct);

        Assert.Equal("{ \"a\": [1, 2] }", (await store.GetAsync(SettingScope.Global, "shape", _ct))!.ValueJson);
    }

    [Fact]
    public async Task TryAddCreatesOnlyWhenMissing()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = CreateStore(db, new StepClock(Start));

        Assert.True(await store.TryAddAsync(SettingScope.Global, "instance", "1", "system", _ct));
        Assert.False(await store.TryAddAsync(SettingScope.Global, "instance", "2", "system", _ct));

        Assert.Equal("1", (await store.GetAsync(SettingScope.Global, "instance", _ct))!.ValueJson);
        var history = Assert.Single(await store.GetHistoryAsync(SettingScope.Global, "instance", 10, _ct));
        Assert.Null(history.OldJson);
        Assert.Equal("system", history.ChangedBy);
    }

    [Fact]
    public async Task TryAddDoesNotOverwriteAnExistingSetting()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = CreateStore(db, new StepClock(Start));
        await store.SetAsync(SettingScope.Global, "limit", "10", "owner", _ct);

        Assert.False(await store.TryAddAsync(SettingScope.Global, "limit", "99", "system", _ct));

        var current = await store.GetAsync(SettingScope.Global, "limit", _ct);
        Assert.Equal("10", current!.ValueJson);
        Assert.Equal(1, current.Version);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("not json")]
    [InlineData("")]
    public async Task InvalidJsonIsRefusedBeforeItReachesTheDatabase(string json)
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = CreateStore(db, new StepClock(Start));

        await Assert.ThrowsAsync<ArgumentException>(() => store.SetAsync(SettingScope.Global, "k", json, "o", _ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.TryAddAsync(SettingScope.Global, "k", json, "o", _ct));
        Assert.Equal(0, await db.CountAsync("settings", _ct));
    }

    [Fact]
    public async Task BlankKeyOrAuthorIsRefused()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = CreateStore(db, new StepClock(Start));

        await Assert.ThrowsAsync<ArgumentException>(() => store.SetAsync(SettingScope.Global, " ", "1", "o", _ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SetAsync(SettingScope.Global, "k", "1", " ", _ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.GetAsync(SettingScope.Global, " ", _ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.GetHistoryAsync(SettingScope.Global, " ", 5, _ct));
    }

    [Fact]
    public async Task ANullValueIsRefused()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = CreateStore(db, new StepClock(Start));

        await Assert.ThrowsAsync<ArgumentNullException>(() => store.SetAsync(SettingScope.Global, "k", null!, "o", _ct));
    }
}
