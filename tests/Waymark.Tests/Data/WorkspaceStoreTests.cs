using Waymark.Data.Stores;
using Waymark.Tests.Support;

namespace Waymark.Tests.Data;

public sealed class WorkspaceStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);

    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task AddsAnEnabledWorkspaceThatHasNeverBeenIndexed()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = new WorkspaceStore(db.Connections, db.Writes);

        var added = await store.AddAsync("alpha", "C:/code/alpha", Now, _ct);

        Assert.NotNull(added);
        Assert.True(added.Id > 0);
        Assert.Equal("alpha", added.Name);
        Assert.Equal("C:/code/alpha", added.Path);
        Assert.True(added.Enabled);
        Assert.Equal(Now, added.AddedUtc);
        Assert.Null(added.LastIndexedUtc);
    }

    [Fact]
    public async Task ADuplicateNameReturnsNullEvenInAnotherCase()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = new WorkspaceStore(db.Connections, db.Writes);
        await store.AddAsync("alpha", "C:/one", Now, _ct);

        Assert.Null(await store.AddAsync("alpha", "C:/two", Now, _ct));
        Assert.Null(await store.AddAsync("ALPHA", "C:/three", Now, _ct));
        Assert.Single(await store.ListAsync(_ct));
    }

    [Fact]
    public async Task ADuplicatePathReturnsNullEvenInAnotherCase()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = new WorkspaceStore(db.Connections, db.Writes);
        await store.AddAsync("alpha", "C:/Code/Same", Now, _ct);

        Assert.Null(await store.AddAsync("beta", "C:/Code/Same", Now, _ct));
        Assert.Null(await store.AddAsync("gamma", "c:/code/same", Now, _ct));
        Assert.Single(await store.ListAsync(_ct));
    }

    [Theory]
    [InlineData("", "C:/x")]
    [InlineData("   ", "C:/x")]
    [InlineData("x", "")]
    [InlineData("x", "  ")]
    public async Task ABlankNameOrPathIsRefused(string name, string path)
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = new WorkspaceStore(db.Connections, db.Writes);

        await Assert.ThrowsAsync<ArgumentException>(() => store.AddAsync(name, path, Now, _ct));
    }

    [Fact]
    public async Task ListsWorkspacesByName()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = new WorkspaceStore(db.Connections, db.Writes);
        await store.AddAsync("charlie", "C:/c", Now, _ct);
        await store.AddAsync("alpha", "C:/a", Now, _ct);
        await store.AddAsync("bravo", "C:/b", Now, _ct);

        var all = await store.ListAsync(_ct);

        Assert.Equal(["alpha", "bravo", "charlie"], all.Select(w => w.Name).ToList());
    }

    [Fact]
    public async Task ListIsEmptyForAFreshDatabase()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = new WorkspaceStore(db.Connections, db.Writes);

        Assert.Empty(await store.ListAsync(_ct));
    }

    [Fact]
    public async Task FindsAWorkspaceByNameIgnoringCase()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = new WorkspaceStore(db.Connections, db.Writes);
        var added = await store.AddAsync("alpha", "C:/a", Now, _ct);

        var found = await store.GetByNameAsync("ALPHA", _ct);

        Assert.Equal(added, found);
        Assert.Null(await store.GetByNameAsync("missing", _ct));
    }

    [Fact]
    public async Task TogglesEnabled()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = new WorkspaceStore(db.Connections, db.Writes);
        var added = await store.AddAsync("alpha", "C:/a", Now, _ct);

        Assert.True(await store.SetEnabledAsync(added!.Id, false, _ct));
        Assert.False((await store.GetByNameAsync("alpha", _ct))!.Enabled);
        Assert.True(await store.SetEnabledAsync(added.Id, true, _ct));
        Assert.True((await store.GetByNameAsync("alpha", _ct))!.Enabled);
    }

    [Fact]
    public async Task SetEnabledOnAnUnknownIdReturnsFalse()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = new WorkspaceStore(db.Connections, db.Writes);

        Assert.False(await store.SetEnabledAsync(9999, true, _ct));
    }

    [Fact]
    public async Task MarksTheIndexTime()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = new WorkspaceStore(db.Connections, db.Writes);
        var added = await store.AddAsync("alpha", "C:/a", Now, _ct);
        var indexedAt = Now.AddHours(2);

        Assert.True(await store.MarkIndexedAsync(added!.Id, indexedAt, _ct));

        Assert.Equal(indexedAt, (await store.GetByNameAsync("alpha", _ct))!.LastIndexedUtc);
        Assert.False(await store.MarkIndexedAsync(9999, indexedAt, _ct));
    }

    [Fact]
    public async Task RemovesAWorkspaceAndFreesItsNameAndPath()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = new WorkspaceStore(db.Connections, db.Writes);
        var added = await store.AddAsync("alpha", "C:/a", Now, _ct);

        Assert.True(await store.RemoveAsync(added!.Id, _ct));
        Assert.False(await store.RemoveAsync(added.Id, _ct));
        Assert.Null(await store.GetByNameAsync("alpha", _ct));
        Assert.NotNull(await store.AddAsync("alpha", "C:/a", Now, _ct));
    }

    [Fact]
    public async Task EveryWorkspaceGetsItsOwnId()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = new WorkspaceStore(db.Connections, db.Writes);

        var first = await store.AddAsync("a", "C:/a", Now, _ct);
        var second = await store.AddAsync("b", "C:/b", Now, _ct);

        Assert.NotEqual(first!.Id, second!.Id);
    }
}
