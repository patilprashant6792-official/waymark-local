using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Waymark.Data.Sqlite;

public interface ISqliteConnectionFactory
{
    /// <summary>Opens a new connection with the per-connection pragmas applied. The caller disposes it.</summary>
    Task<SqliteConnection> OpenAsync(CancellationToken ct);
}

/// <summary>
/// Pooling is off: opening a SQLite file is cheap, and unpooled connections release the file on dispose.
/// Every connection gets busy_timeout, foreign_keys, synchronous and temp_store; WAL is set once by the migrator
/// because it persists in the database file.
/// </summary>
public sealed class SqliteConnectionFactory : ISqliteConnectionFactory
{
    private readonly string _connectionString;
    private readonly string _pragmas;

    public SqliteConnectionFactory(WaymarkDataOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        DapperSetup.Ensure();

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = options.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();

        var busyMilliseconds = (int)options.BusyTimeout.TotalMilliseconds;
        _pragmas = string.Create(
            CultureInfo.InvariantCulture,
            $"PRAGMA busy_timeout={busyMilliseconds}; PRAGMA foreign_keys=ON; PRAGMA synchronous=NORMAL; PRAGMA temp_store=MEMORY;");
    }

    public async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = _pragmas;
            await command.ExecuteNonQueryAsync(ct);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
