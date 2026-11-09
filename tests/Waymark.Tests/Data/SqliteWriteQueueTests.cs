using Microsoft.Data.Sqlite;
using Waymark.Data.Sqlite;
using Waymark.Tests.Support;

namespace Waymark.Tests.Data;

public sealed class SqliteWriteQueueTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private static async Task InsertWorkspaceAsync(SqliteConnection connection, SqliteTransaction transaction, string name, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO workspaces (name, path, enabled, added_utc) VALUES ($name, $path, 1, '2026-01-01T00:00:00.000Z')";
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$path", "C:/" + name);
        await command.ExecuteNonQueryAsync(ct);
    }

    [Fact]
    public void ANullFactoryIsRefused() =>
        Assert.Throws<ArgumentNullException>(() => new SqliteWriteQueue(null!));

    [Fact]
    public async Task ReturnsTheValueTheWorkProduced()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);

        var result = await db.Writes.ExecuteAsync((_, _, _) => Task.FromResult(42), _ct);

        Assert.Equal(42, result);
    }

    [Fact]
    public async Task CommitsWhenTheWorkReturns()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);

        await db.Writes.RunAsync((connection, transaction, ct) => InsertWorkspaceAsync(connection, transaction, "committed", ct), _ct);

        Assert.Equal(1, await db.CountAsync("workspaces", _ct));
    }

    [Fact]
    public async Task RollsBackAndRethrowsWhenTheWorkThrows()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.Writes.RunAsync(
            async (connection, transaction, ct) =>
            {
                await InsertWorkspaceAsync(connection, transaction, "half-done", ct);
                throw new InvalidOperationException("boom");
            },
            _ct));

        Assert.Equal(0, await db.CountAsync("workspaces", _ct));
    }

    [Fact]
    public async Task AFailedJobDoesNotStopTheNextOne()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => db.Writes.RunAsync((_, _, _) => throw new InvalidOperationException("first fails"), _ct));
        await db.Writes.RunAsync((connection, transaction, ct) => InsertWorkspaceAsync(connection, transaction, "after", ct), _ct);

        Assert.Equal(1, await db.CountAsync("workspaces", _ct));
    }

    [Fact]
    public async Task ADatabaseErrorSurfacesToTheCallerAndLeavesNoRows()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);

        await Assert.ThrowsAsync<SqliteException>(() => db.Writes.RunAsync(
            async (connection, transaction, ct) =>
            {
                await InsertWorkspaceAsync(connection, transaction, "twice", ct);
                await InsertWorkspaceAsync(connection, transaction, "twice", ct);
            },
            _ct));

        Assert.Equal(0, await db.CountAsync("workspaces", _ct));
    }

    [Fact]
    public async Task JobsRunInTheOrderTheyWereQueued()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var order = new List<int>();
        var jobs = new List<Task>();

        for (var i = 0; i < 25; i++)
        {
            var number = i;
            jobs.Add(db.Writes.RunAsync((_, _, _) =>
            {
                order.Add(number);
                return Task.CompletedTask;
            }, _ct));
        }

        await Task.WhenAll(jobs);

        Assert.Equal(Enumerable.Range(0, 25), order);
    }

    [Fact]
    public async Task OnlyOneJobRunsAtATime()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var running = 0;
        var highest = 0;
        var jobs = new List<Task>();

        for (var i = 0; i < 10; i++)
        {
            jobs.Add(db.Writes.RunAsync(async (_, _, ct) =>
            {
                var now = Interlocked.Increment(ref running);
                highest = Math.Max(highest, now);
                await Task.Delay(5, ct);
                Interlocked.Decrement(ref running);
            }, _ct));
        }

        await Task.WhenAll(jobs);

        Assert.Equal(1, highest);
    }

    [Fact]
    public async Task ACancelledTokenStopsTheJob()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var ran = false;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => db.Writes.RunAsync(
            (_, _, _) =>
            {
                ran = true;
                return Task.CompletedTask;
            },
            cts.Token));

        Assert.False(ran);
    }

    [Fact]
    public async Task WorkQueuedBeforeDisposeStillFinishes()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var jobs = new List<Task>();
        for (var i = 0; i < 5; i++)
        {
            var name = "queued-" + i;
            jobs.Add(db.Writes.RunAsync((connection, transaction, ct) => InsertWorkspaceAsync(connection, transaction, name, ct), _ct));
        }

        await db.Writes.DisposeAsync();

        await Task.WhenAll(jobs);
        await using var reopened = await db.Connections.OpenAsync(_ct);
        await using var command = reopened.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM workspaces";
        Assert.Equal(5L, Convert.ToInt64(await command.ExecuteScalarAsync(_ct), System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task WritingAfterDisposeIsRefused()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        await db.Writes.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => db.Writes.RunAsync((_, _, _) => Task.CompletedTask, _ct));
    }

    [Fact]
    public async Task DisposingTwiceIsHarmless()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);

        await db.Writes.DisposeAsync();
        await db.Writes.DisposeAsync();
    }

    [Fact]
    public async Task ANullWorkDelegateIsRefused()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);

        await Assert.ThrowsAsync<ArgumentNullException>(() => db.Writes.RunAsync(null!, _ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => db.Writes.ExecuteAsync<int>(null!, _ct));
    }

    [Fact]
    public async Task AnUnopenableDatabaseFailsTheJobButNotTheQueue()
    {
        using var directory = new TempDirectory();
        var factory = new SqliteConnectionFactory(new WaymarkDataOptions(directory.PathOf("nope/x.db")));
        await using var queue = new SqliteWriteQueue(factory);

        await Assert.ThrowsAsync<SqliteException>(() => queue.RunAsync((_, _, _) => Task.CompletedTask, _ct));
        await Assert.ThrowsAsync<SqliteException>(() => queue.RunAsync((_, _, _) => Task.CompletedTask, _ct));
    }
}
