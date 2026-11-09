using Waymark.Data.Stores;
using Waymark.Tests.Support;

namespace Waymark.Tests.Data;

public sealed class VaultStoreTests
{
    private static readonly DateTimeOffset T1 = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
    private static readonly DateTimeOffset T2 = T1.AddHours(3);

    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task UpsertCreatesTheSecretAndReportsIt()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = new VaultStore(db.Connections, db.Writes);

        var created = await store.UpsertAsync("API_KEY", "protected-1", "...1234", T1, _ct);

        Assert.True(created);
        Assert.Equal("protected-1", await store.GetProtectedAsync("API_KEY", _ct));
    }

    [Fact]
    public async Task UpsertReplacesTheValueAndKeepsTheCreationTime()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = new VaultStore(db.Connections, db.Writes);
        await store.UpsertAsync("API_KEY", "protected-1", "...1111", T1, _ct);

        var created = await store.UpsertAsync("API_KEY", "protected-2", "...2222", T2, _ct);

        Assert.False(created);
        Assert.Equal("protected-2", await store.GetProtectedAsync("API_KEY", _ct));
        var listed = Assert.Single(await store.ListAsync(_ct));
        Assert.Equal("...2222", listed.Hint);
        Assert.Equal(T1, listed.CreatedUtc);
        Assert.Equal(T2, listed.UpdatedUtc);
    }

    [Fact]
    public async Task NamesAreMatchedIgnoringCase()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = new VaultStore(db.Connections, db.Writes);
        await store.UpsertAsync("Api_Key", "protected-1", "h", T1, _ct);

        Assert.Equal("protected-1", await store.GetProtectedAsync("API_KEY", _ct));
        Assert.False(await store.UpsertAsync("API_KEY", "protected-2", "h", T2, _ct));
        Assert.Single(await store.ListAsync(_ct));
    }

    [Fact]
    public async Task GetProtectedReturnsNullForAnUnknownName()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = new VaultStore(db.Connections, db.Writes);

        Assert.Null(await store.GetProtectedAsync("NOPE", _ct));
    }

    [Fact]
    public async Task ListsSecretsByName()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = new VaultStore(db.Connections, db.Writes);
        await store.UpsertAsync("b", "pb", "hb", T1, _ct);
        await store.UpsertAsync("a", "pa", "ha", T1, _ct);

        var listed = await store.ListAsync(_ct);

        Assert.Equal(["a", "b"], listed.Select(s => s.Name).ToList());
        Assert.Equal(["ha", "hb"], listed.Select(s => s.Hint).ToList());
        Assert.Null(listed[0].LastUsedUtc);
    }

    [Fact]
    public async Task ListProtectedReturnsEveryNameAndValue()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = new VaultStore(db.Connections, db.Writes);
        await store.UpsertAsync("a", "pa", "ha", T1, _ct);
        await store.UpsertAsync("b", "pb", "hb", T1, _ct);

        var pairs = (await store.ListProtectedAsync(_ct)).OrderBy(p => p.Key, StringComparer.Ordinal).ToList();

        Assert.Equal(2, pairs.Count);
        Assert.Equal("a", pairs[0].Key);
        Assert.Equal("pa", pairs[0].Value);
        Assert.Equal("b", pairs[1].Key);
        Assert.Equal("pb", pairs[1].Value);
    }

    [Fact]
    public async Task MarkUsedStampsTheLastUseTime()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = new VaultStore(db.Connections, db.Writes);
        await store.UpsertAsync("a", "pa", "ha", T1, _ct);

        Assert.True(await store.MarkUsedAsync("a", T2, _ct));

        Assert.Equal(T2, Assert.Single(await store.ListAsync(_ct)).LastUsedUtc);
        Assert.False(await store.MarkUsedAsync("missing", T2, _ct));
    }

    [Fact]
    public async Task DeleteRemovesTheSecret()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = new VaultStore(db.Connections, db.Writes);
        await store.UpsertAsync("a", "pa", "ha", T1, _ct);

        Assert.True(await store.DeleteAsync("a", _ct));
        Assert.False(await store.DeleteAsync("a", _ct));
        Assert.Null(await store.GetProtectedAsync("a", _ct));
        Assert.Empty(await store.ListAsync(_ct));
    }

    [Fact]
    public async Task BlankArgumentsAreRefused()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = new VaultStore(db.Connections, db.Writes);

        await Assert.ThrowsAsync<ArgumentException>(() => store.UpsertAsync(" ", "p", "h", T1, _ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.UpsertAsync("a", " ", "h", T1, _ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => store.UpsertAsync("a", "p", null!, T1, _ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.GetProtectedAsync(" ", _ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.MarkUsedAsync(" ", T1, _ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.DeleteAsync(" ", _ct));
    }
}
