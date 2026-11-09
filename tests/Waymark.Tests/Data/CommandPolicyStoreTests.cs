using Waymark.Core.Commands;
using Waymark.Data.Stores;
using Waymark.Tests.Support;

namespace Waymark.Tests.Data;

public sealed class CommandPolicyStoreTests
{
    private static readonly DateTimeOffset T1 = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
    private static readonly DateTimeOffset T2 = T1.AddHours(1);

    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task StartsWithoutGatesOrTrust()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = new CommandPolicyStore(db.Connections, db.Writes);

        Assert.Empty(await store.ListGatesAsync(_ct));
        Assert.Null(await store.GetTrustAsync(1, _ct));
    }

    [Theory]
    [InlineData(CommandGate.Off)]
    [InlineData(CommandGate.Ask)]
    [InlineData(CommandGate.Auto)]
    public async Task EveryGateSurvivesTheRoundTrip(CommandGate gate)
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = new CommandPolicyStore(db.Connections, db.Writes);

        await store.SetGateAsync(7, "dotnet.build", gate, T1, _ct);

        var saved = Assert.Single(await store.ListGatesAsync(_ct));
        Assert.Equal(new CommandGateSetting(7, "dotnet.build", gate, T1), saved);
    }

    [Fact]
    public async Task SettingAGateAgainReplacesItInPlace()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = new CommandPolicyStore(db.Connections, db.Writes);
        await store.SetGateAsync(7, "dotnet.build", CommandGate.Ask, T1, _ct);

        await store.SetGateAsync(7, "dotnet.build", CommandGate.Auto, T2, _ct);

        var saved = Assert.Single(await store.ListGatesAsync(_ct));
        Assert.Equal(CommandGate.Auto, saved.Gate);
        Assert.Equal(T2, saved.UpdatedUtc);
    }

    [Fact]
    public async Task GatesAreKeptPerWorkspaceAndOperationWithZeroMeaningEveryWorkspace()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = new CommandPolicyStore(db.Connections, db.Writes);
        await store.SetGateAsync(2, "git.push", CommandGate.Off, T1, _ct);
        await store.SetGateAsync(0, "git.push", CommandGate.Ask, T1, _ct);
        await store.SetGateAsync(0, "dotnet.build", CommandGate.Auto, T1, _ct);
        await store.SetGateAsync(1, "git.push", CommandGate.Auto, T1, _ct);

        var all = await store.ListGatesAsync(_ct);

        Assert.Equal(
            ["0:dotnet.build", "0:git.push", "1:git.push", "2:git.push"],
            all.Select(g => g.WorkspaceId + ":" + g.Operation).ToList());
    }

    [Fact]
    public async Task ClearingAGateRemovesOnlyThatGate()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = new CommandPolicyStore(db.Connections, db.Writes);
        await store.SetGateAsync(1, "git.push", CommandGate.Off, T1, _ct);
        await store.SetGateAsync(1, "dotnet.build", CommandGate.Auto, T1, _ct);

        Assert.True(await store.ClearGateAsync(1, "git.push", _ct));
        Assert.False(await store.ClearGateAsync(1, "git.push", _ct));

        var left = Assert.Single(await store.ListGatesAsync(_ct));
        Assert.Equal("dotnet.build", left.Operation);
    }

    [Fact]
    public async Task TheDatabaseStoresGatesAsLowerCaseText()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = new CommandPolicyStore(db.Connections, db.Writes);
        await store.SetGateAsync(1, "git.push", CommandGate.Auto, T1, _ct);

        Assert.Equal("auto", await db.ScalarTextAsync("SELECT gate FROM command_gates", _ct));
    }

    [Fact]
    public async Task BlankOperationsAreRefused()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = new CommandPolicyStore(db.Connections, db.Writes);

        await Assert.ThrowsAsync<ArgumentException>(() => store.SetGateAsync(1, " ", CommandGate.Ask, T1, _ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ClearGateAsync(1, " ", _ct));
    }

    [Fact]
    public async Task TrustIsStoredPerWorkspaceAndReplaced()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var workspaces = new WorkspaceStore(db.Connections, db.Writes);
        var workspace = await workspaces.AddAsync("alpha", "C:/alpha", T1, _ct);
        var store = new CommandPolicyStore(db.Connections, db.Writes);

        await store.SetTrustAsync(workspace!.Id, "{\"a\":1}", T1, _ct);
        Assert.Equal(new CommandTrust(workspace.Id, "{\"a\":1}", T1), await store.GetTrustAsync(workspace.Id, _ct));

        await store.SetTrustAsync(workspace.Id, "{\"a\":2}", T2, _ct);
        Assert.Equal(new CommandTrust(workspace.Id, "{\"a\":2}", T2), await store.GetTrustAsync(workspace.Id, _ct));
        Assert.Equal(1, await db.CountAsync("command_trust", _ct));
    }

    [Fact]
    public async Task ClearingTrustReportsWhetherThereWasAny()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var workspaces = new WorkspaceStore(db.Connections, db.Writes);
        var workspace = await workspaces.AddAsync("alpha", "C:/alpha", T1, _ct);
        var store = new CommandPolicyStore(db.Connections, db.Writes);
        await store.SetTrustAsync(workspace!.Id, "{}", T1, _ct);

        Assert.True(await store.ClearTrustAsync(workspace.Id, _ct));
        Assert.False(await store.ClearTrustAsync(workspace.Id, _ct));
        Assert.Null(await store.GetTrustAsync(workspace.Id, _ct));
    }

    [Fact]
    public async Task RemovingAWorkspaceRemovesItsTrustButKeepsItsGates()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var workspaces = new WorkspaceStore(db.Connections, db.Writes);
        var workspace = await workspaces.AddAsync("alpha", "C:/alpha", T1, _ct);
        var store = new CommandPolicyStore(db.Connections, db.Writes);
        await store.SetTrustAsync(workspace!.Id, "{}", T1, _ct);
        await store.SetGateAsync(workspace.Id, "git.push", CommandGate.Off, T1, _ct);

        await workspaces.RemoveAsync(workspace.Id, _ct);

        Assert.Null(await store.GetTrustAsync(workspace.Id, _ct));
        Assert.Single(await store.ListGatesAsync(_ct));
    }

    [Fact]
    public async Task ANullFingerprintIsRefused()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = new CommandPolicyStore(db.Connections, db.Writes);

        await Assert.ThrowsAsync<ArgumentNullException>(() => store.SetTrustAsync(1, null!, T1, _ct));
    }
}
