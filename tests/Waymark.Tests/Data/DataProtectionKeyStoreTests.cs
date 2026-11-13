using Waymark.Data.Stores;
using Waymark.Tests.Support;

namespace Waymark.Tests.Data;

public sealed class DataProtectionKeyStoreTests
{
    private static readonly DateTimeOffset T1 = new(2026, 11, 13, 10, 0, 0, TimeSpan.Zero);

    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task AnEmptyRingHasNoElements()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = new DataProtectionKeyStore(db.Connections, db.Writes);

        Assert.Empty(await store.ListAsync(_ct));
    }

    [Fact]
    public async Task ElementsComeBackExactlyAndInTheOrderTheyWereAdded()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = new DataProtectionKeyStore(db.Connections, db.Writes);

        await store.AddAsync("key-1", "<key id=\"1\" />", T1, _ct);
        await store.AddAsync("key-2", "<key id=\"2\" />", T1.AddDays(90), _ct);

        Assert.Equal(["<key id=\"1\" />", "<key id=\"2\" />"], await store.ListAsync(_ct));
        Assert.Equal(2, await db.CountAsync("data_protection_keys", _ct));
    }

    [Fact]
    public async Task AnEmptyElementIsRefused()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = new DataProtectionKeyStore(db.Connections, db.Writes);

        await Assert.ThrowsAsync<ArgumentException>(() => store.AddAsync("key", " ", T1, _ct));
    }
}
