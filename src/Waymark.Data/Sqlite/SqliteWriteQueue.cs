using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace Waymark.Data.Sqlite;

/// <summary>
/// Runs every write on one dedicated connection, one transaction per job, in arrival order.
/// One writer means no "database is locked" errors and no lost updates between a read and a write in the same job.
/// </summary>
public interface IWriteQueue
{
    /// <summary>Runs the work in a transaction: committed when it returns, rolled back when it throws.</summary>
    Task<T> ExecuteAsync<T>(Func<SqliteConnection, SqliteTransaction, CancellationToken, Task<T>> work, CancellationToken ct);

    Task RunAsync(Func<SqliteConnection, SqliteTransaction, CancellationToken, Task> work, CancellationToken ct);
}

public sealed class SqliteWriteQueue : IWriteQueue, IAsyncDisposable
{
    private const int Capacity = 1024;

    private readonly ISqliteConnectionFactory _connections;
    private readonly Channel<WriteJob> _channel = Channel.CreateBounded<WriteJob>(
        new BoundedChannelOptions(Capacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Task _loop;
    private int _disposed;

    public SqliteWriteQueue(ISqliteConnectionFactory connections)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _loop = Task.Run(LoopAsync);
    }

    public async Task<T> ExecuteAsync<T>(Func<SqliteConnection, SqliteTransaction, CancellationToken, Task<T>> work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(work);
        var job = new WriteJob<T>(work, ct);
        try
        {
            await _channel.Writer.WriteAsync(job, ct);
        }
        catch (ChannelClosedException)
        {
            throw new ObjectDisposedException(nameof(SqliteWriteQueue));
        }

        return await job.Completion;
    }

    public Task RunAsync(Func<SqliteConnection, SqliteTransaction, CancellationToken, Task> work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(work);
        return ExecuteAsync<bool>(
            async (connection, transaction, token) =>
            {
                await work(connection, transaction, token);
                return true;
            },
            ct);
    }

    /// <summary>Stops accepting work, finishes what is queued, then closes the writer connection.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _channel.Writer.TryComplete();
        await _loop;
    }

    private async Task LoopAsync()
    {
        SqliteConnection? connection = null;
        try
        {
            await foreach (var job in _channel.Reader.ReadAllAsync())
            {
                try
                {
                    connection ??= await _connections.OpenAsync(CancellationToken.None);
                }
                catch (Exception ex)
                {
                    job.Fail(ex);
                    continue;
                }

                await job.RunAsync(connection);
            }
        }
        finally
        {
            if (connection is not null)
            {
                await connection.DisposeAsync();
            }
        }
    }

    private abstract class WriteJob
    {
        public abstract Task RunAsync(SqliteConnection connection);

        public abstract void Fail(Exception exception);
    }

    private sealed class WriteJob<T>(Func<SqliteConnection, SqliteTransaction, CancellationToken, Task<T>> work, CancellationToken ct) : WriteJob
    {
        private readonly TaskCompletionSource<T> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<T> Completion => _completion.Task;

        public override async Task RunAsync(SqliteConnection connection)
        {
            if (ct.IsCancellationRequested)
            {
                _completion.TrySetCanceled(ct);
                return;
            }

            try
            {
                await using var transaction = connection.BeginTransaction();
                var result = await work(connection, transaction, ct);
                await transaction.CommitAsync(ct);
                _completion.TrySetResult(result);
            }
            catch (Exception ex)
            {
                _completion.TrySetException(ex);
            }
        }

        public override void Fail(Exception exception) => _completion.TrySetException(exception);
    }
}
