using Microsoft.Data.Sqlite;
using Waymark.Data.Sqlite;
using Waymark.Tests.Support;

namespace Waymark.Tests.Data;

public sealed class SqliteConnectionFactoryTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public void NullOptionsAreRefused() =>
        Assert.Throws<ArgumentNullException>(() => new SqliteConnectionFactory(null!));

    [Fact]
    public async Task ForeignKeysAreOnForEveryConnection()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);

        Assert.Equal(1, await db.ScalarLongAsync("PRAGMA foreign_keys;", _ct));
    }

    [Fact]
    public async Task TheBusyTimeoutComesFromTheOptions()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);

        Assert.Equal(5000, await db.ScalarLongAsync("PRAGMA busy_timeout;", _ct));
    }

    [Fact]
    public async Task AConfiguredBusyTimeoutIsApplied()
    {
        using var directory = new TempDirectory();
        var options = new WaymarkDataOptions(directory.PathOf("custom.db"), TimeSpan.FromSeconds(2));
        var factory = new SqliteConnectionFactory(options);

        await using var connection = await factory.OpenAsync(_ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout;";
        var value = await command.ExecuteScalarAsync(_ct);

        Assert.Equal(2000L, Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task EachCallReturnsItsOwnOpenConnection()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);

        await using var first = await db.Connections.OpenAsync(_ct);
        await using var second = await db.Connections.OpenAsync(_ct);

        Assert.NotSame(first, second);
        Assert.Equal(System.Data.ConnectionState.Open, first.State);
        Assert.Equal(System.Data.ConnectionState.Open, second.State);
    }

    [Fact]
    public async Task AnUnopenableFileFailsInsteadOfReturningABrokenConnection()
    {
        using var directory = new TempDirectory();
        var options = new WaymarkDataOptions(directory.PathOf("missing-folder/x.db"));
        var factory = new SqliteConnectionFactory(options);

        await Assert.ThrowsAsync<SqliteException>(() => factory.OpenAsync(_ct));
    }

    [Fact]
    public async Task ACancelledTokenStopsTheOpen()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => db.Connections.OpenAsync(cts.Token));
    }
}
