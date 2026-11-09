using Waymark.Core.Security;
using Waymark.Data.Stores;
using Waymark.Tests.Support;

namespace Waymark.Tests.Data;

public sealed class AccessRuleStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);

    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private async Task<(TestDatabase Db, WorkspaceStore Workspaces, AccessRuleStore Rules, long WorkspaceId)> CreateAsync()
    {
        var db = await TestDatabase.CreateAsync(_ct);
        var workspaces = new WorkspaceStore(db.Connections, db.Writes);
        var workspace = await workspaces.AddAsync("alpha", "C:/alpha", Now, _ct);
        return (db, workspaces, new AccessRuleStore(db.Connections, db.Writes), workspace!.Id);
    }

    [Fact]
    public async Task AddsAnEnabledRule()
    {
        var (db, _, rules, workspaceId) = await CreateAsync();
        await using var _db = db;

        var added = await rules.AddAsync(workspaceId, AccessRuleKind.BlockFolder, "legacy", "old code", Now, _ct);

        Assert.NotNull(added);
        Assert.True(added.Id > 0);
        Assert.Equal(workspaceId, added.WorkspaceId);
        Assert.Equal(AccessRuleKind.BlockFolder, added.Kind);
        Assert.Equal("legacy", added.Pattern);
        Assert.Equal("old code", added.Note);
        Assert.True(added.Enabled);
        Assert.Equal(Now, added.CreatedUtc);
    }

    [Fact]
    public async Task ADuplicateRuleReturnsNullEvenInAnotherCase()
    {
        var (db, _, rules, workspaceId) = await CreateAsync();
        await using var _db = db;
        await rules.AddAsync(workspaceId, AccessRuleKind.BlockKeyword, "secret", string.Empty, Now, _ct);

        Assert.Null(await rules.AddAsync(workspaceId, AccessRuleKind.BlockKeyword, "secret", "again", Now, _ct));
        Assert.Null(await rules.AddAsync(workspaceId, AccessRuleKind.BlockKeyword, "SECRET", string.Empty, Now, _ct));
        Assert.Single(await rules.ListAsync(workspaceId, _ct));
    }

    [Fact]
    public async Task TheSamePatternUnderAnotherKindIsNotADuplicate()
    {
        var (db, _, rules, workspaceId) = await CreateAsync();
        await using var _db = db;
        await rules.AddAsync(workspaceId, AccessRuleKind.BlockKeyword, "notes", string.Empty, Now, _ct);

        var other = await rules.AddAsync(workspaceId, AccessRuleKind.BlockFileName, "notes", string.Empty, Now, _ct);

        Assert.NotNull(other);
    }

    [Fact]
    public async Task TheSameRuleInAnotherWorkspaceIsNotADuplicate()
    {
        var (db, workspaces, rules, workspaceId) = await CreateAsync();
        await using var _db = db;
        var second = await workspaces.AddAsync("beta", "C:/beta", Now, _ct);
        await rules.AddAsync(workspaceId, AccessRuleKind.BlockFolder, "legacy", string.Empty, Now, _ct);

        var added = await rules.AddAsync(second!.Id, AccessRuleKind.BlockFolder, "legacy", string.Empty, Now, _ct);

        Assert.NotNull(added);
        Assert.Single(await rules.ListAsync(workspaceId, _ct));
        Assert.Single(await rules.ListAsync(second.Id, _ct));
    }

    [Fact]
    public async Task ListsRulesByKindThenPattern()
    {
        var (db, _, rules, workspaceId) = await CreateAsync();
        await using var _db = db;
        await rules.AddAsync(workspaceId, AccessRuleKind.BlockKeyword, "zeta", string.Empty, Now, _ct);
        await rules.AddAsync(workspaceId, AccessRuleKind.AllowExtension, ".proto", string.Empty, Now, _ct);
        await rules.AddAsync(workspaceId, AccessRuleKind.BlockFolder, "b", string.Empty, Now, _ct);
        await rules.AddAsync(workspaceId, AccessRuleKind.BlockFolder, "a", string.Empty, Now, _ct);

        var listed = await rules.ListAsync(workspaceId, _ct);

        Assert.Equal(
            ["AllowExtension:.proto", "BlockFolder:a", "BlockFolder:b", "BlockKeyword:zeta"],
            listed.Select(r => r.Kind + ":" + r.Pattern).ToList());
    }

    [Fact]
    public async Task ListIsEmptyForAWorkspaceWithoutRules()
    {
        var (db, _, rules, workspaceId) = await CreateAsync();
        await using var _db = db;

        Assert.Empty(await rules.ListAsync(workspaceId, _ct));
        Assert.Empty(await rules.ListAsync(9999, _ct));
    }

    [Fact]
    public async Task GetsARuleById()
    {
        var (db, _, rules, workspaceId) = await CreateAsync();
        await using var _db = db;
        var added = await rules.AddAsync(workspaceId, AccessRuleKind.BlockFileName, "*.sql", "no sql", Now, _ct);

        Assert.Equal(added, await rules.GetAsync(added!.Id, _ct));
        Assert.Null(await rules.GetAsync(9999, _ct));
    }

    [Fact]
    public async Task EveryKindSurvivesTheRoundTrip()
    {
        var (db, _, rules, workspaceId) = await CreateAsync();
        await using var _db = db;

        foreach (var kind in Enum.GetValues<AccessRuleKind>())
        {
            var added = await rules.AddAsync(workspaceId, kind, "pattern-" + kind, string.Empty, Now, _ct);
            Assert.Equal(kind, (await rules.GetAsync(added!.Id, _ct))!.Kind);
        }
    }

    [Fact]
    public async Task TogglesEnabled()
    {
        var (db, _, rules, workspaceId) = await CreateAsync();
        await using var _db = db;
        var added = await rules.AddAsync(workspaceId, AccessRuleKind.BlockFolder, "legacy", string.Empty, Now, _ct);

        Assert.True(await rules.SetEnabledAsync(added!.Id, false, _ct));
        Assert.False((await rules.GetAsync(added.Id, _ct))!.Enabled);
        Assert.True(await rules.SetEnabledAsync(added.Id, true, _ct));
        Assert.True((await rules.GetAsync(added.Id, _ct))!.Enabled);
        Assert.False(await rules.SetEnabledAsync(9999, true, _ct));
    }

    [Fact]
    public async Task RemovesARule()
    {
        var (db, _, rules, workspaceId) = await CreateAsync();
        await using var _db = db;
        var added = await rules.AddAsync(workspaceId, AccessRuleKind.BlockFolder, "legacy", string.Empty, Now, _ct);

        Assert.True(await rules.RemoveAsync(added!.Id, _ct));
        Assert.False(await rules.RemoveAsync(added.Id, _ct));
        Assert.Null(await rules.GetAsync(added.Id, _ct));
    }

    [Fact]
    public async Task RemovingAWorkspaceRemovesItsRules()
    {
        var (db, workspaces, rules, workspaceId) = await CreateAsync();
        await using var _db = db;
        await rules.AddAsync(workspaceId, AccessRuleKind.BlockFolder, "legacy", string.Empty, Now, _ct);
        await rules.AddAsync(workspaceId, AccessRuleKind.BlockKeyword, "secret", string.Empty, Now, _ct);

        await workspaces.RemoveAsync(workspaceId, _ct);

        Assert.Empty(await rules.ListAsync(workspaceId, _ct));
        Assert.Equal(0, await db.CountAsync("workspace_access_rules", _ct));
    }

    [Fact]
    public async Task ABlankPatternOrNullNoteIsRefused()
    {
        var (db, _, rules, workspaceId) = await CreateAsync();
        await using var _db = db;

        await Assert.ThrowsAsync<ArgumentException>(
            () => rules.AddAsync(workspaceId, AccessRuleKind.BlockFolder, "  ", string.Empty, Now, _ct));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => rules.AddAsync(workspaceId, AccessRuleKind.BlockFolder, "legacy", null!, Now, _ct));
    }
}
