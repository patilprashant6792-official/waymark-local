using Microsoft.Data.Sqlite;
using Waymark.Core.Auth;
using Waymark.Data.Stores;
using Waymark.Tests.Support;

namespace Waymark.Tests.Data;

public sealed class OAuthClientStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);

    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private static OAuthClient Client(string id, DateTimeOffset? registered = null, DateTimeOffset? lastUsed = null, DateTimeOffset? revoked = null) =>
        new(id, "name-" + id, ["http://localhost/cb", "http://127.0.0.1/cb"], registered ?? Now, lastUsed, revoked);

    private static string ApprovalSql(string clientId, string pollHex, DateTimeOffset expires, string status) =>
        "INSERT INTO approvals (poll_hash, pairing_code, client_id, client_name_claimed, redirect_uri, redirect_host, state, code_challenge, scope, resource, requested_utc, expires_utc, status) VALUES (X'"
        + pollHex + "', '1234', '" + clientId + "', 'n', 'http://localhost/cb', 'localhost', NULL, 'ch', 's', 'r', '2026-01-01T00:00:00.000Z', '"
        + expires.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture) + "', '" + status + "')";

    private static string TokenSql(string clientId, string hashHex) =>
        "INSERT INTO tokens (token_hash, kind, family_id, client_id, scope, resource, issued_utc, expires_utc) VALUES (X'"
        + hashHex + "', 'access', 'fam', '" + clientId + "', 's', 'r', '2026-01-01T00:00:00.000Z', '2030-01-01T00:00:00.000Z')";

    [Fact]
    public async Task StoresAndReadsAClientBack()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = new OAuthClientStore(db.Connections, db.Writes);

        await store.AddAsync(Client("c1"), _ct);

        var loaded = await store.GetAsync("c1", _ct);
        Assert.NotNull(loaded);
        Assert.Equal("c1", loaded.ClientId);
        Assert.Equal("name-c1", loaded.ClientName);
        Assert.Equal(["http://localhost/cb", "http://127.0.0.1/cb"], loaded.RedirectUris.ToList());
        Assert.Equal(Now, loaded.RegisteredUtc);
        Assert.Null(loaded.LastUsedUtc);
        Assert.False(loaded.IsRevoked);
    }

    [Fact]
    public async Task AnUnknownClientIsNull()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = new OAuthClientStore(db.Connections, db.Writes);

        Assert.Null(await store.GetAsync("nope", _ct));
    }

    [Fact]
    public async Task ADuplicateClientIdThrows()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = new OAuthClientStore(db.Connections, db.Writes);
        await store.AddAsync(Client("c1"), _ct);

        await Assert.ThrowsAsync<SqliteException>(() => store.AddAsync(Client("c1"), _ct));
    }

    [Fact]
    public async Task AClientNeedsAtLeastOneRedirectUri()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = new OAuthClientStore(db.Connections, db.Writes);
        var none = new OAuthClient("c1", "n", [], Now, null, null);

        await Assert.ThrowsAsync<ArgumentException>(() => store.AddAsync(none, _ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => store.AddAsync(null!, _ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.AddAsync(Client(" "), _ct));
    }

    [Fact]
    public async Task ListsNewestRegistrationFirst()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = new OAuthClientStore(db.Connections, db.Writes);
        await store.AddAsync(Client("old", Now), _ct);
        await store.AddAsync(Client("new", Now.AddHours(1)), _ct);

        var listed = await store.ListAsync(_ct);

        Assert.Equal(["new", "old"], listed.Select(c => c.ClientId).ToList());
    }

    [Fact]
    public async Task TouchRecordsTheLastUse()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = new OAuthClientStore(db.Connections, db.Writes);
        await store.AddAsync(Client("c1"), _ct);

        await store.TouchAsync("c1", Now.AddMinutes(5), _ct);

        Assert.Equal(Now.AddMinutes(5), (await store.GetAsync("c1", _ct))!.LastUsedUtc);
    }

    [Fact]
    public async Task RevokingAClientAlsoRevokesItsTokensAndOnlyItsTokens()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = new OAuthClientStore(db.Connections, db.Writes);
        await store.AddAsync(Client("c1"), _ct);
        await store.AddAsync(Client("c2"), _ct);
        await db.ExecuteAsync(TokenSql("c1", "01"), _ct);
        await db.ExecuteAsync(TokenSql("c1", "02"), _ct);
        await db.ExecuteAsync(TokenSql("c2", "03"), _ct);

        Assert.True(await store.RevokeAsync("c1", Now.AddMinutes(1), _ct));

        Assert.True((await store.GetAsync("c1", _ct))!.IsRevoked);
        Assert.False((await store.GetAsync("c2", _ct))!.IsRevoked);
        Assert.Equal(2, await db.ScalarLongAsync("SELECT COUNT(*) FROM tokens WHERE client_id = 'c1' AND revoked_utc IS NOT NULL", _ct));
        Assert.Equal(0, await db.ScalarLongAsync("SELECT COUNT(*) FROM tokens WHERE client_id = 'c2' AND revoked_utc IS NOT NULL", _ct));
    }

    [Fact]
    public async Task RevokingTwiceOrAnUnknownClientReturnsFalse()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = new OAuthClientStore(db.Connections, db.Writes);
        await store.AddAsync(Client("c1"), _ct);
        await store.RevokeAsync("c1", Now.AddMinutes(1), _ct);

        Assert.False(await store.RevokeAsync("c1", Now.AddMinutes(2), _ct));
        Assert.False(await store.RevokeAsync("nope", Now, _ct));
        Assert.Equal(Now.AddMinutes(1), (await store.GetAsync("c1", _ct))!.RevokedUtc);
    }

    [Fact]
    public async Task PruneRemovesOnlyOldNeverUsedClientsWithoutRequests()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = new OAuthClientStore(db.Connections, db.Writes);
        var cutoff = Now;
        await store.AddAsync(Client("stale", cutoff.AddDays(-2)), _ct);
        await store.AddAsync(Client("used", cutoff.AddDays(-2), lastUsed: cutoff.AddDays(-1)), _ct);
        await store.AddAsync(Client("recent", cutoff.AddHours(1)), _ct);
        await store.AddAsync(Client("asked", cutoff.AddDays(-2)), _ct);
        await db.ExecuteAsync(ApprovalSql("asked", "AA01", Now.AddHours(1), "pending"), _ct);

        var removed = await store.PruneUnusedAsync(cutoff, _ct);

        Assert.Equal(1, removed);
        Assert.Null(await store.GetAsync("stale", _ct));
        Assert.NotNull(await store.GetAsync("used", _ct));
        Assert.NotNull(await store.GetAsync("recent", _ct));
        Assert.NotNull(await store.GetAsync("asked", _ct));
    }

    [Fact]
    public async Task ClearDisconnectedRemovesRevokedClientsWithTheirTokensAndRequests()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = new OAuthClientStore(db.Connections, db.Writes);
        await store.AddAsync(Client("gone", revoked: Now.AddDays(-1)), _ct);
        await store.AddAsync(Client("active"), _ct);
        await db.ExecuteAsync(ApprovalSql("gone", "AA01", Now.AddDays(-1), "expired"), _ct);
        await db.ExecuteAsync(TokenSql("gone", "01"), _ct);
        await db.ExecuteAsync(TokenSql("active", "02"), _ct);

        var removed = await store.ClearDisconnectedAsync(Now, _ct);

        Assert.Equal(1, removed);
        Assert.Null(await store.GetAsync("gone", _ct));
        Assert.NotNull(await store.GetAsync("active", _ct));
        Assert.Equal(0, await db.ScalarLongAsync("SELECT COUNT(*) FROM tokens WHERE client_id = 'gone'", _ct));
        Assert.Equal(1, await db.ScalarLongAsync("SELECT COUNT(*) FROM tokens WHERE client_id = 'active'", _ct));
        Assert.Equal(0, await db.CountAsync("approvals", _ct));
    }

    [Fact]
    public async Task ClearDisconnectedKeepsARevokedClientWithAWaitingRequest()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = new OAuthClientStore(db.Connections, db.Writes);
        await store.AddAsync(Client("waiting", revoked: Now.AddDays(-1)), _ct);
        await db.ExecuteAsync(ApprovalSql("waiting", "AA01", Now.AddMinutes(10), "pending"), _ct);

        var removed = await store.ClearDisconnectedAsync(Now, _ct);

        Assert.Equal(0, removed);
        Assert.NotNull(await store.GetAsync("waiting", _ct));
        Assert.Equal(1, await db.CountAsync("approvals", _ct));
    }

    [Fact]
    public async Task ClearDisconnectedTreatsAnExpiredPendingRequestAsNotWaiting()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = new OAuthClientStore(db.Connections, db.Writes);
        await store.AddAsync(Client("stale", revoked: Now.AddDays(-1)), _ct);
        await db.ExecuteAsync(ApprovalSql("stale", "AA01", Now.AddMinutes(-10), "pending"), _ct);

        Assert.Equal(1, await store.ClearDisconnectedAsync(Now, _ct));
        Assert.Equal(0, await db.CountAsync("approvals", _ct));
    }

    [Fact]
    public async Task ClearDisconnectedWithNothingToClearReturnsZero()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = new OAuthClientStore(db.Connections, db.Writes);
        await store.AddAsync(Client("active"), _ct);

        Assert.Equal(0, await store.ClearDisconnectedAsync(Now, _ct));
        Assert.NotNull(await store.GetAsync("active", _ct));
    }
}
