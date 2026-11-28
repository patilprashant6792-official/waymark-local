using Waymark.Core.Commands;
using Waymark.Data.Stores;
using Waymark.Tests.Support;

namespace Waymark.Tests.Data;

/// <summary>Deleting history: only finished one-time runs of the place asked for go; active runs and terminal runs stay.</summary>
public sealed class CommandRunHistoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 28, 6, 0, 0, TimeSpan.Zero);

    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task DeletingAFinishedRunRemovesItAndReturnsIt()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = new CommandRunStore(db.Connections, db.Writes);
        await AddWorkspacesAsync(db);
        var run = await AddRunAsync(store, 1, CommandRunStatus.Succeeded, outputPath: "/runs/run-1.log");

        var deleted = await store.DeleteFinishedAsync(run.Id, _ct);

        Assert.NotNull(deleted);
        Assert.Equal(run.Id, deleted.Id);
        Assert.Equal("/runs/run-1.log", deleted.OutputPath);
        Assert.Null(await store.GetAsync(run.Id, _ct));
    }

    [Theory]
    [InlineData(CommandRunStatus.WaitingApproval)]
    [InlineData(CommandRunStatus.Queued)]
    [InlineData(CommandRunStatus.Running)]
    public async Task AnActiveRunIsNotDeleted(CommandRunStatus status)
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = new CommandRunStore(db.Connections, db.Writes);
        await AddWorkspacesAsync(db);
        var run = await AddRunAsync(store, 1, status);

        Assert.Null(await store.DeleteFinishedAsync(run.Id, _ct));
        Assert.NotNull(await store.GetAsync(run.Id, _ct));
    }

    [Fact]
    public async Task ARunOfATerminalIsNotDeleted()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = new CommandRunStore(db.Connections, db.Writes);
        await AddWorkspacesAsync(db);
        var terminalId = await AddTerminalAsync(db);
        var run = await AddRunAsync(store, 1, CommandRunStatus.Cancelled, terminalId);

        Assert.Null(await store.DeleteFinishedAsync(run.Id, _ct));
        Assert.NotNull(await store.GetAsync(run.Id, _ct));
    }

    [Fact]
    public async Task AnUnknownRunIsNotDeleted()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = new CommandRunStore(db.Connections, db.Writes);

        Assert.Null(await store.DeleteFinishedAsync(4242, _ct));
    }

    [Fact]
    public async Task ClearingAWorkspaceDeletesOnlyItsFinishedOneTimeRuns()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = new CommandRunStore(db.Connections, db.Writes);
        await AddWorkspacesAsync(db);
        var terminalId = await AddTerminalAsync(db);
        var ok = await AddRunAsync(store, 1, CommandRunStatus.Succeeded);
        var failed = await AddRunAsync(store, 1, CommandRunStatus.Failed);
        var denied = await AddRunAsync(store, 1, CommandRunStatus.Denied);
        var running = await AddRunAsync(store, 1, CommandRunStatus.Running);
        var inTerminal = await AddRunAsync(store, 1, CommandRunStatus.Cancelled, terminalId);
        var otherWorkspace = await AddRunAsync(store, 2, CommandRunStatus.Succeeded);
        var system = await AddRunAsync(store, null, CommandRunStatus.Succeeded);

        var deleted = await store.DeleteFinishedInAsync(1, _ct);

        Assert.Equal([ok.Id, failed.Id, denied.Id], deleted.Select(r => r.Id).Order());
        foreach (var kept in new[] { running, inTerminal, otherWorkspace, system })
        {
            Assert.NotNull(await store.GetAsync(kept.Id, _ct));
        }
    }

    [Fact]
    public async Task ClearingTheComputerDeletesOnlyRunsOutsideEveryWorkspace()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = new CommandRunStore(db.Connections, db.Writes);
        await AddWorkspacesAsync(db);
        var system = await AddRunAsync(store, null, CommandRunStatus.TimedOut);
        var systemRunning = await AddRunAsync(store, null, CommandRunStatus.Running);
        var inWorkspace = await AddRunAsync(store, 1, CommandRunStatus.Succeeded);

        var deleted = await store.DeleteFinishedInAsync(null, _ct);

        Assert.Equal([system.Id], deleted.Select(r => r.Id));
        Assert.NotNull(await store.GetAsync(systemRunning.Id, _ct));
        Assert.NotNull(await store.GetAsync(inWorkspace.Id, _ct));
    }

    [Fact]
    public async Task ClearingAnEmptyPlaceDeletesNothing()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = new CommandRunStore(db.Connections, db.Writes);
        await AddWorkspacesAsync(db);

        Assert.Empty(await store.DeleteFinishedInAsync(2, _ct));
    }

    private async Task AddWorkspacesAsync(TestDatabase db)
    {
        await db.ExecuteAsync("INSERT INTO workspaces (id, name, path, enabled, added_utc) VALUES (1, 'one', '/ws/one', 1, '2026-01-01T00:00:00.000Z')", _ct);
        await db.ExecuteAsync("INSERT INTO workspaces (id, name, path, enabled, added_utc) VALUES (2, 'two', '/ws/two', 1, '2026-01-01T00:00:00.000Z')", _ct);
    }

    private async Task<long> AddTerminalAsync(TestDatabase db)
    {
        await db.ExecuteAsync(
            "INSERT INTO terminals (workspace_id, workspace_name, name, folder, state, created_utc, updated_utc) " +
            "VALUES (1, 'one', 'web', '/ws/one', 'stopped', '2026-01-01T00:00:00.000Z', '2026-01-01T00:00:00.000Z')",
            _ct);
        return await db.ScalarLongAsync("SELECT id FROM terminals WHERE name = 'web'", _ct);
    }

    private async Task<CommandRun> AddRunAsync(CommandRunStore store, long? workspaceId, CommandRunStatus status, long? terminalId = null, string? outputPath = null)
    {
        var name = workspaceId switch { 1 => "one", 2 => "two", _ => "system" };
        var created = await store.CreateAsync(
            new NewCommandRun(workspaceId, name, CommandTier.Recipe, "dotnet.build", null, "dotnet build", "/ws", "test", status, "claude", null, [], Now, terminalId),
            _ct);
        if (!status.IsActive() || outputPath is not null)
        {
            await store.UpdateAsync(created.Id, new CommandRunUpdate { EndedUtc = status.IsActive() ? null : Now, OutputPath = outputPath }, _ct);
        }

        return (await store.GetAsync(created.Id, _ct))!;
    }
}
