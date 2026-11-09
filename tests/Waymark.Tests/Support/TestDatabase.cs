using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Waymark.Data.Sqlite;

namespace Waymark.Tests.Support;

/// <summary>A real SQLite file in a throw-away folder, migrated with the production migrator. Dispose it to release the file.</summary>
internal sealed class TestDatabase : IAsyncDisposable
{
    private readonly TempDirectory _directory = new();

    private TestDatabase()
    {
        Options = WaymarkDataOptions.ForDirectory(_directory.Root);
        Connections = new SqliteConnectionFactory(Options);
        Writes = new SqliteWriteQueue(Connections);
    }

    public WaymarkDataOptions Options { get; }

    public SqliteConnectionFactory Connections { get; }

    public SqliteWriteQueue Writes { get; }

    public static async Task<TestDatabase> CreateAsync(CancellationToken ct, bool migrate = true)
    {
        var database = new TestDatabase();
        if (!migrate)
        {
            return database;
        }

        try
        {
            await database.CreateMigrator().MigrateAsync(ct);
            return database;
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    public Migrator CreateMigrator() => new(Connections, NullLogger<Migrator>.Instance);

    public async Task<object?> ScalarAsync(string sql, CancellationToken ct)
    {
        await using var connection = await Connections.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(ct);
    }

    public async Task<long> ScalarLongAsync(string sql, CancellationToken ct) =>
        Convert.ToInt64(await ScalarAsync(sql, ct), CultureInfo.InvariantCulture);

    public async Task<string?> ScalarTextAsync(string sql, CancellationToken ct) =>
        Convert.ToString(await ScalarAsync(sql, ct), CultureInfo.InvariantCulture);

    public Task<long> CountAsync(string table, CancellationToken ct) =>
        ScalarLongAsync("SELECT COUNT(*) FROM " + table, ct);

    public async Task ExecuteAsync(string sql, CancellationToken ct)
    {
        await using var connection = await Connections.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        await Writes.DisposeAsync();
        _directory.Dispose();
    }
}

/// <summary>A clock the test moves by hand.</summary>
internal sealed class StepClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
