using Microsoft.Data.Sqlite;
using Waymark.Core.Auth;
using Waymark.Data.Stores;
using Waymark.Tests.Support;

namespace Waymark.Tests.Data;

public sealed class AccessKeyStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);

    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private static NewAccessKey NewKey(string id, byte[] hash, DateTimeOffset? created = null, DateTimeOffset? expires = null, string name = "ci") =>
        new(id, name, hash, ".." + id, "read", created ?? Now, expires);

    [Fact]
    public async Task CreatedKeysAreFoundByTheirHash()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = new AccessKeyStore(db.Connections, db.Writes);
        await store.CreateAsync(NewKey("k1", [1, 2, 3]), _ct);

        var found = await store.FindActiveAsync([1, 2, 3], Now, _ct);

        Assert.NotNull(found);
        Assert.Equal("k1", found.Id);
        Assert.Equal("ci", found.Name);
        Assert.Equal("..k1", found.Hint);
        Assert.Equal("read", found.Scope);
        Assert.Equal(Now, found.CreatedUtc);
        Assert.Null(found.ExpiresUtc);
        Assert.Null(found.LastUsedUtc);
        Assert.False(found.IsRevoked);
    }

    [Fact]
    public async Task AnUnknownHashIsNotFound()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = new AccessKeyStore(db.Connections, db.Writes);
        await store.CreateAsync(NewKey("k1", [1, 2, 3]), _ct);

        Assert.Null(await store.FindActiveAsync([9, 9, 9], Now, _ct));
        Assert.Null(await store.FindActiveAsync([], Now, _ct));
    }

    [Fact]
    public async Task ARevokedKeyIsNoLongerActive()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = new AccessKeyStore(db.Connections, db.Writes);
        await store.CreateAsync(NewKey("k1", [1]), _ct);

        Assert.True(await store.RevokeAsync("k1", Now.AddMinutes(1), _ct));

        Assert.Null(await store.FindActiveAsync([1], Now.AddMinutes(2), _ct));
        var listed = Assert.Single(await store.ListAsync(_ct));
        Assert.True(listed.IsRevoked);
        Assert.Equal(Now.AddMinutes(1), listed.RevokedUtc);
    }

    [Fact]
    public async Task RevokingTwiceOrAnUnknownKeyReturnsFalseAndKeepsTheFirstTime()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = new AccessKeyStore(db.Connections, db.Writes);
        await store.CreateAsync(NewKey("k1", [1]), _ct);
        await store.RevokeAsync("k1", Now.AddMinutes(1), _ct);

        Assert.False(await store.RevokeAsync("k1", Now.AddMinutes(9), _ct));
        Assert.False(await store.RevokeAsync("nope", Now, _ct));
        Assert.Equal(Now.AddMinutes(1), Assert.Single(await store.ListAsync(_ct)).RevokedUtc);
    }

    [Fact]
    public async Task AnExpiredKeyIsNotActiveFromItsExpiryTime()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = new AccessKeyStore(db.Connections, db.Writes);
        var expires = Now.AddDays(1);
        await store.CreateAsync(NewKey("k1", [1], expires: expires), _ct);

        Assert.NotNull(await store.FindActiveAsync([1], expires.AddMilliseconds(-1), _ct));
        Assert.Null(await store.FindActiveAsync([1], expires, _ct));
        Assert.Null(await store.FindActiveAsync([1], expires.AddDays(1), _ct));
    }

    [Fact]
    public async Task ListsNewestFirst()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = new AccessKeyStore(db.Connections, db.Writes);
        await store.CreateAsync(NewKey("old", [1], created: Now), _ct);
        await store.CreateAsync(NewKey("new", [2], created: Now.AddHours(1)), _ct);
        await store.CreateAsync(NewKey("mid", [3], created: Now.AddMinutes(30)), _ct);

        var listed = await store.ListAsync(_ct);

        Assert.Equal(["new", "mid", "old"], listed.Select(k => k.Id).ToList());
    }

    [Fact]
    public async Task TouchRecordsTheLastUse()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = new AccessKeyStore(db.Connections, db.Writes);
        await store.CreateAsync(NewKey("k1", [1]), _ct);

        await store.TouchAsync("k1", Now.AddHours(2), _ct);

        Assert.Equal(Now.AddHours(2), Assert.Single(await store.ListAsync(_ct)).LastUsedUtc);
    }

    [Fact]
    public async Task TouchingAnUnknownKeyIsHarmless()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = new AccessKeyStore(db.Connections, db.Writes);

        await store.TouchAsync("nope", Now, _ct);

        Assert.Empty(await store.ListAsync(_ct));
    }

    [Fact]
    public async Task ADuplicateIdOrHashIsRejected()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = new AccessKeyStore(db.Connections, db.Writes);
        await store.CreateAsync(NewKey("k1", [1]), _ct);

        await Assert.ThrowsAsync<SqliteException>(() => store.CreateAsync(NewKey("k1", [2]), _ct));
        await Assert.ThrowsAsync<SqliteException>(() => store.CreateAsync(NewKey("k2", [1]), _ct));
        Assert.Single(await store.ListAsync(_ct));
    }

    [Fact]
    public async Task TheDatabaseNeverHoldsAnythingButTheHash()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = new AccessKeyStore(db.Connections, db.Writes);
        await store.CreateAsync(NewKey("k1", [0xAB, 0xCD]), _ct);

        Assert.Equal("ABCD", await db.ScalarTextAsync("SELECT hex(key_hash) FROM api_keys", _ct));
    }

    [Fact]
    public async Task InvalidArgumentsAreRefused()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = new AccessKeyStore(db.Connections, db.Writes);

        await Assert.ThrowsAsync<ArgumentNullException>(() => store.CreateAsync(null!, _ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.CreateAsync(NewKey(" ", [1]), _ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.CreateAsync(NewKey("k", [1], name: " "), _ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => store.FindActiveAsync(null!, Now, _ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.RevokeAsync(" ", Now, _ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.TouchAsync(" ", Now, _ct));
    }
}
