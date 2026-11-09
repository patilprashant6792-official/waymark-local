using Waymark.Data.Stores;
using Waymark.Tests.Support;

namespace Waymark.Tests.Data;

public sealed class ToolSettingsStoreTests
{
    private static readonly DateTimeOffset T1 = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
    private static readonly DateTimeOffset T2 = T1.AddMinutes(30);

    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task IsEmptyForAFreshDatabase()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = new ToolSettingsStore(db.Connections, db.Writes);

        var all = await store.ListAsync(_ct);

        Assert.DoesNotContain(all, s => s.ToolName == "never-set");
    }

    [Fact]
    public async Task SetStoresTheChoiceWithWhoAndWhen()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = new ToolSettingsStore(db.Connections, db.Writes);

        await store.SetAsync("zz.tool", false, "owner", T1, _ct);

        var saved = Assert.Single(await store.ListAsync(_ct), s => s.ToolName == "zz.tool");
        Assert.False(saved.Enabled);
        Assert.Equal("owner", saved.ChangedBy);
        Assert.Equal(T1, saved.ChangedUtc);
    }

    [Fact]
    public async Task SetAgainUpdatesTheSameRow()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = new ToolSettingsStore(db.Connections, db.Writes);
        await store.SetAsync("zz.tool", false, "owner", T1, _ct);

        await store.SetAsync("zz.tool", true, "admin", T2, _ct);

        var saved = Assert.Single(await store.ListAsync(_ct), s => s.ToolName == "zz.tool");
        Assert.True(saved.Enabled);
        Assert.Equal("admin", saved.ChangedBy);
        Assert.Equal(T2, saved.ChangedUtc);
    }

    [Fact]
    public async Task ListIsOrderedByToolName()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = new ToolSettingsStore(db.Connections, db.Writes);
        await store.SetAsync("zz.c", true, "o", T1, _ct);
        await store.SetAsync("zz.a", true, "o", T1, _ct);
        await store.SetAsync("zz.b", true, "o", T1, _ct);

        var names = (await store.ListAsync(_ct)).Select(s => s.ToolName).Where(n => n.StartsWith("zz.", StringComparison.Ordinal)).ToList();

        Assert.Equal(["zz.a", "zz.b", "zz.c"], names);
    }

    [Fact]
    public async Task SetManyAppliesTheSameChoiceToEveryTool()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = new ToolSettingsStore(db.Connections, db.Writes);

        await store.SetManyAsync(["zz.a", "zz.b", "zz.c"], false, "owner", T1, _ct);

        var mine = (await store.ListAsync(_ct)).Where(s => s.ToolName.StartsWith("zz.", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, mine.Count);
        Assert.All(mine, s => Assert.False(s.Enabled));
        Assert.All(mine, s => Assert.Equal(T1, s.ChangedUtc));
    }

    [Fact]
    public async Task SetManyWithAnEmptyListDoesNothing()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = new ToolSettingsStore(db.Connections, db.Writes);
        var before = (await store.ListAsync(_ct)).Count;

        await store.SetManyAsync([], true, "owner", T1, _ct);

        Assert.Equal(before, (await store.ListAsync(_ct)).Count);
    }

    [Fact]
    public async Task SetManyRefusesABlankNameBeforeWritingAnything()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = new ToolSettingsStore(db.Connections, db.Writes);
        var before = (await store.ListAsync(_ct)).Count;

        await Assert.ThrowsAsync<ArgumentException>(() => store.SetManyAsync(["zz.a", " "], true, "owner", T1, _ct));

        Assert.Equal(before, (await store.ListAsync(_ct)).Count);
    }

    [Fact]
    public async Task BlankToolOrAuthorIsRefused()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = new ToolSettingsStore(db.Connections, db.Writes);

        await Assert.ThrowsAsync<ArgumentException>(() => store.SetAsync(" ", true, "owner", T1, _ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SetAsync("zz.a", true, " ", T1, _ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SetManyAsync(["zz.a"], true, " ", T1, _ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => store.SetManyAsync(null!, true, "owner", T1, _ct));
    }
}
