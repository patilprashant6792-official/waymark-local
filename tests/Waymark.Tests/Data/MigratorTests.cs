using Microsoft.Data.Sqlite;
using Waymark.Data.Sqlite;
using Waymark.Tests.Support;

namespace Waymark.Tests.Data;

public sealed class MigratorTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private static int EmbeddedMigrationCount() =>
        typeof(Migrator).Assembly.GetManifestResourceNames()
            .Count(name => name.StartsWith("Waymark.Data.Migrations.", StringComparison.Ordinal)
                && name.EndsWith(".sql", StringComparison.Ordinal));

    [Fact]
    public async Task MigratesAnEmptyDatabaseToTheLatestVersion()
    {
        await using var db = await TestDatabase.CreateAsync(_ct, migrate: false);

        var version = await db.CreateMigrator().MigrateAsync(_ct);

        Assert.True(version > 0);
        Assert.Equal(EmbeddedMigrationCount(), version);
        Assert.Equal(version, await db.ScalarLongAsync("PRAGMA user_version;", _ct));
    }

    [Fact]
    public async Task RunningItTwiceChangesNothing()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        await db.ExecuteAsync("INSERT INTO workspaces (name, path, enabled, added_utc) VALUES ('keep', 'C:/keep', 1, '2026-01-01T00:00:00.000Z')", _ct);

        var again = await db.CreateMigrator().MigrateAsync(_ct);

        Assert.Equal(EmbeddedMigrationCount(), again);
        Assert.Equal(1, await db.CountAsync("workspaces", _ct));
    }

    [Theory]
    [InlineData("workspaces")]
    [InlineData("settings")]
    [InlineData("settings_history")]
    [InlineData("oauth_clients")]
    [InlineData("approvals")]
    [InlineData("vault_secrets")]
    [InlineData("workspace_access_rules")]
    [InlineData("command_runs")]
    [InlineData("command_gates")]
    [InlineData("command_trust")]
    public async Task CreatesTheTable(string table)
    {
        await using var db = await TestDatabase.CreateAsync(_ct);

        var found = await db.ScalarLongAsync(
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = '" + table + "'",
            _ct);

        Assert.Equal(1, found);
    }

    [Fact]
    public async Task SwitchesTheDatabaseToWriteAheadLogging()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);

        var mode = await db.ScalarTextAsync("PRAGMA journal_mode;", _ct);

        Assert.Equal("wal", mode, ignoreCase: true);
    }

    [Fact]
    public async Task RefusesADatabaseFromANewerBuild()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        await db.ExecuteAsync("PRAGMA user_version = 9999;", _ct);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => db.CreateMigrator().MigrateAsync(_ct));

        Assert.Contains("9999", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TablesAreStrict()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);

        await Assert.ThrowsAsync<SqliteException>(() => db.ExecuteAsync(
            "INSERT INTO workspaces (name, path, enabled, added_utc) VALUES ('x', 'C:/x', 'not a number', '2026-01-01T00:00:00.000Z')",
            _ct));
    }

    [Fact]
    public async Task ASettingWithInvalidJsonIsRejectedByTheDatabase()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);

        await Assert.ThrowsAsync<SqliteException>(() => db.ExecuteAsync(
            "INSERT INTO settings (scope_kind, scope_id, key, value_json, version, updated_utc) VALUES ('global', '', 'k', '{oops', 1, '2026-01-01T00:00:00.000Z')",
            _ct));
    }

    [Fact]
    public async Task AGlobalSettingWithAScopeIdIsRejectedByTheDatabase()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);

        await Assert.ThrowsAsync<SqliteException>(() => db.ExecuteAsync(
            "INSERT INTO settings (scope_kind, scope_id, key, value_json, version, updated_utc) VALUES ('global', '7', 'k', '1', 1, '2026-01-01T00:00:00.000Z')",
            _ct));
    }
}
