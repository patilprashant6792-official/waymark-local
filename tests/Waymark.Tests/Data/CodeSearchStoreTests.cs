using Waymark.Core.Code;
using Waymark.Data.Stores;
using Waymark.Modules.Code.Search;
using Waymark.Tests.Support;

namespace Waymark.Tests.Data;

public sealed class CodeSearchStoreTests
{
    private static readonly IReadOnlySet<CodeTypeKind> AllTypes = new HashSet<CodeTypeKind>(Enum.GetValues<CodeTypeKind>());
    private static readonly IReadOnlySet<CodeMemberKind> AllMembers = new HashSet<CodeMemberKind>(Enum.GetValues<CodeMemberKind>());
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private static async Task<(CodeIndexStore Index, CodeSearchStore Search, long WorkspaceId)> SetUpAsync(TestDatabase db, CancellationToken ct)
    {
        await db.ExecuteAsync("INSERT INTO workspaces (name, path, enabled, added_utc) VALUES ('ws', 'C:/ws', 1, '2026-01-01T00:00:00.000Z')", ct);
        var workspaceId = await db.ScalarLongAsync("SELECT id FROM workspaces WHERE name = 'ws'", ct);
        var index = new CodeIndexStore(db.Connections, db.Writes);
        await index.ReplaceFileAsync(workspaceId, TokenServiceFile(), DateTimeOffset.UnixEpoch, ct);
        return (index, new CodeSearchStore(db.Connections), workspaceId);
    }

    private static CodeFileIndex TokenServiceFile() =>
        new(
            new CodeFileStamp("src/Auth/TokenService.cs", "hash1", 100, DateTimeOffset.UnixEpoch),
            40,
            "source",
            [],
            [new CodeTypeEntry(CodeTypeKind.Class, "TokenService", "Acme.Auth.TokenService", "Acme.Auth", null, "public", null, "Issues and rotates tokens", 1, 40)],
            [
                new CodeMemberEntry(0, CodeMemberKind.Method, "Rotate", "Task Rotate(string refreshToken)", "Task", "public", null, null, 10, 20, "refresh token revoke session"),
                new CodeMemberEntry(0, CodeMemberKind.Method, "Issue", "Task Issue(string userId)", "Task", "public", null, null, 22, 30, "sign claims expiry")
            ],
            [
                new CodeCallSiteEntry("TokenService", "Issue", "Rotate", null, 25),
                new CodeCallSiteEntry("TokenService", "Issue", "Rotate", "this", 27),
                new CodeCallSiteEntry("TokenService", "Issue", "Sign", null, 28)
            ]);

    private CodeContentQuery Content(long workspaceId, string expression, IReadOnlySet<CodeTypeKind>? types = null, IReadOnlySet<CodeMemberKind>? members = null, int limit = 50) =>
        new([workspaceId], expression, types ?? AllTypes, members ?? AllMembers, limit);

    [Fact]
    public async Task FindsAMemberByAWordFromItsBody()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var (_, search, workspaceId) = await SetUpAsync(db, _ct);

        var result = await search.FindByContentAsync(Content(workspaceId, FtsQuery.Build(["revoke"], FtsQuery.CodeColumns, all: true)!), _ct);

        var hit = Assert.Single(result.Hits);
        Assert.Equal("Rotate", hit.Row.Name);
        Assert.Equal("TokenService", hit.Row.Owner);
        Assert.False(hit.Row.IsType);
        Assert.True(hit.Relevance > 0);
    }

    [Fact]
    public async Task StemmingFindsOtherFormsOfAWord()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var (_, search, workspaceId) = await SetUpAsync(db, _ct);

        var result = await search.FindByContentAsync(Content(workspaceId, FtsQuery.Build(["rotating"], FtsQuery.DocColumns, all: true)!), _ct);

        var hit = Assert.Single(result.Hits);
        Assert.Equal("TokenService", hit.Row.Name);
        Assert.True(hit.Row.IsType);
    }

    [Fact]
    public async Task AWordPrefixMatches()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var (_, search, workspaceId) = await SetUpAsync(db, _ct);

        var result = await search.FindByContentAsync(Content(workspaceId, FtsQuery.Build(["revo"], FtsQuery.CodeColumns, all: true)!), _ct);

        Assert.Single(result.Hits);
    }

    [Fact]
    public async Task SplitNameWordsFindACamelCaseName()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var (_, search, workspaceId) = await SetUpAsync(db, _ct);

        var result = await search.FindByContentAsync(Content(workspaceId, FtsQuery.Build(["service"], "{tokens}", all: true)!), _ct);

        Assert.Contains(result.Hits, h => h.Row.Name == "TokenService");
    }

    [Fact]
    public async Task TheOwnersNameWordsFindItsMembers()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var (_, search, workspaceId) = await SetUpAsync(db, _ct);

        var result = await search.FindByContentAsync(Content(workspaceId, FtsQuery.Build(["rotate", "service"], "{tokens}", all: true)!), _ct);

        var hit = Assert.Single(result.Hits);
        Assert.Equal("Rotate", hit.Row.Name);
    }

    [Fact]
    public async Task AllWordsMustMatchWithAndButAnyWordWithOr()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var (_, search, workspaceId) = await SetUpAsync(db, _ct);

        var all = await search.FindByContentAsync(Content(workspaceId, FtsQuery.Build(["revoke", "expiry"], FtsQuery.CodeColumns, all: true)!), _ct);
        var any = await search.FindByContentAsync(Content(workspaceId, FtsQuery.Build(["revoke", "expiry"], FtsQuery.CodeColumns, all: false)!), _ct);

        Assert.Empty(all.Hits);
        Assert.Equal(2, any.Hits.Count);
    }

    [Fact]
    public async Task AnotherWorkspaceFindsNothing()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var (_, search, workspaceId) = await SetUpAsync(db, _ct);

        var result = await search.FindByContentAsync(Content(workspaceId + 999, FtsQuery.Build(["revoke"], FtsQuery.CodeColumns, all: true)!), _ct);

        Assert.Empty(result.Hits);
    }

    [Fact]
    public async Task KindsLimitTheResult()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var (_, search, workspaceId) = await SetUpAsync(db, _ct);
        var expression = FtsQuery.Build(["token"], FtsQuery.AllColumns, all: true)!;

        var onlyTypes = await search.FindByContentAsync(Content(workspaceId, expression, members: new HashSet<CodeMemberKind>()), _ct);
        var onlyMembers = await search.FindByContentAsync(Content(workspaceId, expression, types: new HashSet<CodeTypeKind>()), _ct);

        Assert.All(onlyTypes.Hits, h => Assert.True(h.Row.IsType));
        Assert.All(onlyMembers.Hits, h => Assert.False(h.Row.IsType));
        Assert.NotEmpty(onlyTypes.Hits);
        Assert.NotEmpty(onlyMembers.Hits);
    }

    [Fact]
    public async Task TheLimitCapsTheHits()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var (_, search, workspaceId) = await SetUpAsync(db, _ct);

        var result = await search.FindByContentAsync(Content(workspaceId, FtsQuery.Build(["token"], FtsQuery.AllColumns, all: true)!, limit: 1), _ct);

        Assert.Single(result.Hits);
        Assert.True(result.Capped);
    }

    [Fact]
    public async Task ABadExpressionIsAnArgumentError()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var (_, search, workspaceId) = await SetUpAsync(db, _ct);

        await Assert.ThrowsAsync<ArgumentException>(() => search.FindByContentAsync(Content(workspaceId, "\"unterminated"), _ct));
    }

    [Fact]
    public async Task AReplacedFileLeavesNoStaleWords()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var (index, search, workspaceId) = await SetUpAsync(db, _ct);
        var changed = TokenServiceFile() with { Members = [new CodeMemberEntry(0, CodeMemberKind.Method, "Rotate", "Task Rotate()", "Task", "public", null, null, 10, 20, "brandnew")] };

        await index.ReplaceFileAsync(workspaceId, changed, DateTimeOffset.UnixEpoch, _ct);

        var stale = await search.FindByContentAsync(Content(workspaceId, FtsQuery.Build(["revoke"], FtsQuery.CodeColumns, all: true)!), _ct);
        var fresh = await search.FindByContentAsync(Content(workspaceId, FtsQuery.Build(["brandnew"], FtsQuery.CodeColumns, all: true)!), _ct);
        Assert.Empty(stale.Hits);
        Assert.Single(fresh.Hits);
        Assert.Equal(await db.CountAsync("code_members", _ct) + await db.CountAsync("code_types", _ct), await db.CountAsync("code_fts", _ct));
    }

    [Fact]
    public async Task ARemovedFileLeavesNoRowsInTheFullTextIndex()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var (index, _, workspaceId) = await SetUpAsync(db, _ct);

        await index.RemoveFileAsync(workspaceId, "src/Auth/TokenService.cs", _ct);

        Assert.Equal(0, await db.CountAsync("code_fts", _ct));
    }

    [Fact]
    public async Task CountsTheCallersOfEachName()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var (_, search, workspaceId) = await SetUpAsync(db, _ct);

        var counts = await search.CountCallersAsync([workspaceId], ["Rotate", "Sign", "Missing", "rotate"], _ct);

        Assert.Equal(2, counts["Rotate"]);
        Assert.Equal(1, counts["Sign"]);
        Assert.False(counts.ContainsKey("Missing"));
        Assert.False(counts.ContainsKey("rotate"));
    }

    [Fact]
    public async Task AnExactNameIsReturnedFirstByTheNameLookup()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var (_, search, workspaceId) = await SetUpAsync(db, _ct);

        var result = await search.FindSymbolsAsync(
            new CodeSymbolQuery([workspaceId], ["rotate"], false, AllTypes, AllMembers, false, false, 100),
            _ct);

        Assert.Equal("Rotate", result.Rows[0].Name);
    }

    [Fact]
    public async Task TheNameLookupReturnsTheDocSummary()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var (_, search, workspaceId) = await SetUpAsync(db, _ct);

        var result = await search.FindSymbolsAsync(
            new CodeSymbolQuery([workspaceId], ["TokenService"], false, AllTypes, new HashSet<CodeMemberKind>(), false, false, 100),
            _ct);

        Assert.Equal("Issues and rotates tokens", result.Rows[0].Doc);
    }
}
