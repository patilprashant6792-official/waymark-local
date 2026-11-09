using Microsoft.Extensions.Logging.Abstractions;
using Waymark.Core.Audit;
using Waymark.Data.Stores;
using Waymark.Tests.Support;

namespace Waymark.Tests.Data;

public sealed class AuditStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);

    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private static AuditStore NewStore(TestDatabase db) => new(db.Connections, db.Writes, NullLogger<AuditStore>.Instance);

    private static AuditEntry Entry(
        string action,
        AuditCategory category = AuditCategory.Tool,
        AuditOutcome outcome = AuditOutcome.Ok,
        int minutes = 0,
        string? target = "ws",
        string? actor = "claude",
        string? error = null,
        int? bytes = null,
        int? tokens = null,
        string? purpose = null) =>
        new(Now.AddMinutes(minutes), category, actor, action, target, null, 12, bytes, outcome, error, tokens, purpose);

    private static ToolPayload Payload(string input = "in", string output = "out") =>
        new(input, false, input.Length, output, false, output.Length);

    [Fact]
    public async Task AnEntryComesBackWithEveryField()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = NewStore(db);
        var entry = new AuditEntry(Now, AuditCategory.Config, "owner", "setting.changed", "edge.public_url", "{\"a\":1}", 42, 100, AuditOutcome.Denied, "nope", 25, "why");
        await store.AppendAsync(entry, _ct);

        var stored = (await store.QueryAsync(new AuditQuery(), _ct)).Single();
        var byId = await store.GetAsync(stored.Id, _ct);

        Assert.Equal(entry, stored.Entry);
        Assert.NotNull(byId);
        Assert.Equal(entry, byId.Entry);
    }

    [Fact]
    public async Task AMissingEntryIsNull()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);

        Assert.Null(await NewStore(db).GetAsync(999, _ct));
    }

    [Fact]
    public async Task NullEntriesAreRefused()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = NewStore(db);

        await Assert.ThrowsAsync<ArgumentNullException>(() => store.AppendAsync(null!, _ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => store.AppendBatchAsync(null!, _ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => store.AppendToolCallAsync(null!, null, _ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => store.QueryAsync(null!, _ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => store.CountAsync(null!, _ct));
    }

    [Fact]
    public async Task BatchesAreStoredAndAnEmptyBatchDoesNothing()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = NewStore(db);

        await store.AppendBatchAsync([], _ct);
        Assert.Equal(0, await db.CountAsync("audit_entries", _ct));

        await store.AppendBatchAsync([Entry("a"), Entry("b"), Entry("c")], _ct);
        Assert.Equal(3, await db.CountAsync("audit_entries", _ct));
    }

    [Fact]
    public async Task QueryIsNewestFirstAndPagesByIdInBothDirections()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = NewStore(db);
        await store.AppendBatchAsync([Entry("a"), Entry("b"), Entry("c"), Entry("d")], _ct);

        var all = await store.QueryAsync(new AuditQuery(), _ct);

        Assert.Equal(["d", "c", "b", "a"], all.Select(e => e.Entry.Action).ToArray());
        var older = await store.QueryAsync(new AuditQuery(BeforeId: all[1].Id), _ct);
        Assert.Equal(["b", "a"], older.Select(e => e.Entry.Action).ToArray());
        var newer = await store.QueryAsync(new AuditQuery(AfterId: all[2].Id), _ct);
        Assert.Equal(["d", "c"], newer.Select(e => e.Entry.Action).ToArray());
        var limited = await store.QueryAsync(new AuditQuery(Limit: 2), _ct);
        Assert.Equal(["d", "c"], limited.Select(e => e.Entry.Action).ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(501)]
    public async Task TheQueryLimitIsBounded(int limit)
    {
        await using var db = await TestDatabase.CreateAsync(_ct);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => NewStore(db).QueryAsync(new AuditQuery(Limit: limit), _ct));
    }

    [Fact]
    public async Task TheLimitBoundariesAreAllowed()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = NewStore(db);

        Assert.Empty(await store.QueryAsync(new AuditQuery(Limit: 1), _ct));
        Assert.Empty(await store.QueryAsync(new AuditQuery(Limit: 500), _ct));
    }

    [Fact]
    public async Task FiltersCombineWithAnd()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = NewStore(db);
        await store.AppendBatchAsync(
            [
                Entry("code_read", AuditCategory.Tool, AuditOutcome.Ok, 0, "ws1"),
                Entry("code_read", AuditCategory.Tool, AuditOutcome.Error, 1, "ws2", error: "boom"),
                Entry("file_write", AuditCategory.Tool, AuditOutcome.Ok, 2, "ws1"),
                Entry("sign-in", AuditCategory.Auth, AuditOutcome.Denied, 3, null),
            ],
            _ct);

        Assert.Equal(3, (await store.QueryAsync(new AuditQuery(AuditCategory.Tool), _ct)).Count);
        Assert.Single(await store.QueryAsync(new AuditQuery(Outcome: AuditOutcome.Denied), _ct));
        Assert.Equal(2, (await store.QueryAsync(new AuditQuery(Action: "code_read"), _ct)).Count);
        Assert.Equal(2, (await store.QueryAsync(new AuditQuery(Target: "ws1"), _ct)).Count);
        Assert.Single(await store.QueryAsync(new AuditQuery(Action: "code_read", Target: "ws2"), _ct));
        Assert.Single(await store.QueryAsync(new AuditQuery(AuditCategory.Tool, AuditOutcome.Error), _ct));
        Assert.Equal(2, (await store.QueryAsync(new AuditQuery(SinceUtc: Now.AddMinutes(2)), _ct)).Count);
        Assert.Equal(2, await store.CountAsync(new AuditQuery(Action: "code_read"), _ct));
    }

    [Fact]
    public async Task BlankActionAndTargetFiltersAreIgnoredAndValuesAreTrimmed()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = NewStore(db);
        await store.AppendBatchAsync([Entry("a"), Entry("b")], _ct);

        Assert.Equal(2, (await store.QueryAsync(new AuditQuery(Action: "  ", Target: "", Text: " "), _ct)).Count);
        Assert.Single(await store.QueryAsync(new AuditQuery(Action: "  a  "), _ct));
    }

    [Fact]
    public async Task TextSearchIsCaseInsensitiveAndCoversTheDocumentedColumns()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = NewStore(db);
        await store.AppendBatchAsync(
            [
                Entry("ReadThing"),
                Entry("x", target: "MyWorkspace"),
                Entry("y", actor: "Alice"),
                Entry("z", error: "Disk Full"),
                Entry("w", purpose: "verify the Build"),
                Entry("other"),
            ],
            _ct);

        async Task<string[]> Find(string text) =>
            (await store.QueryAsync(new AuditQuery(Text: text), _ct)).Select(e => e.Entry.Action).ToArray();

        Assert.Equal(["ReadThing"], await Find("readthing"));
        Assert.Equal(["x"], await Find("myworkspace"));
        Assert.Equal(["y"], await Find("ALICE"));
        Assert.Equal(["z"], await Find("disk full"));
        Assert.Equal(["w"], await Find("build"));
        Assert.Empty(await Find("nothing-like-this"));
    }

    [Fact]
    public async Task TextSearchTreatsPercentUnderscoreAndBackslashLiterally()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = NewStore(db);
        await store.AppendBatchAsync(
            [
                Entry("a_b"),
                Entry("axb"),
                Entry("half", error: "50% done"),
                Entry("full", error: "50 percent"),
                Entry("path", error: "C:\\temp"),
                Entry("nopath", error: "C:/temp"),
            ],
            _ct);

        async Task<string[]> Find(string text) =>
            (await store.QueryAsync(new AuditQuery(Text: text), _ct)).Select(e => e.Entry.Action).ToArray();

        Assert.Equal(["a_b"], await Find("a_b"));
        Assert.Equal(["half"], await Find("50%"));
        Assert.Equal(["path"], await Find("C:\\temp"));
    }

    [Fact]
    public async Task DistinctActionsAreSortedAndPerCategory()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = NewStore(db);
        await store.AppendBatchAsync(
            [Entry("b"), Entry("a"), Entry("b"), Entry("sign-in", AuditCategory.Auth)],
            _ct);

        Assert.Equal(["a", "b"], (await store.DistinctActionsAsync(AuditCategory.Tool, _ct)).ToArray());
        Assert.Equal(["sign-in"], (await store.DistinctActionsAsync(AuditCategory.Auth, _ct)).ToArray());
        Assert.Empty(await store.DistinctActionsAsync(AuditCategory.Config, _ct));
    }

    [Fact]
    public async Task DistinctTargetsSkipNullAndEmptyOnes()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = NewStore(db);
        await store.AppendBatchAsync(
            [Entry("a", target: "zeta"), Entry("b", target: "alpha"), Entry("c", target: null), Entry("d", target: string.Empty), Entry("e", target: "alpha")],
            _ct);

        Assert.Equal(["alpha", "zeta"], (await store.DistinctTargetsAsync(AuditCategory.Tool, _ct)).ToArray());
    }

    [Fact]
    public async Task AToolCallWithAPayloadStoresBothAndReturnsTheRowId()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = NewStore(db);

        var id = await store.AppendToolCallAsync(Entry("code_read"), new ToolPayload("the input", true, 5000, "the output", false, 10), _ct);

        var stored = await store.GetAsync(id, _ct);
        var payload = await store.GetPayloadAsync(id, _ct);
        Assert.NotNull(stored);
        Assert.Equal("code_read", stored.Entry.Action);
        Assert.NotNull(payload);
        Assert.Equal("the input", payload.Input);
        Assert.True(payload.InputCut);
        Assert.Equal(5000, payload.InputChars);
        Assert.False(payload.OutputCut);
        Assert.Equal(10, payload.OutputChars);
        Assert.Equal(Now, payload.StoredUtc);
        Assert.Equal("the output", await store.GetPayloadOutputAsync(id, _ct));
    }

    [Fact]
    public async Task AToolCallWithoutAPayloadHasNone()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = NewStore(db);

        var id = await store.AppendToolCallAsync(Entry("code_read"), null, _ct);

        Assert.NotNull(await store.GetAsync(id, _ct));
        Assert.Null(await store.GetPayloadAsync(id, _ct));
        Assert.Null(await store.GetPayloadOutputAsync(id, _ct));
        Assert.Equal(0, await db.CountAsync("audit_payloads", _ct));
    }

    [Fact]
    public async Task IdsOfSeveralToolCallsAreDistinctAndIncreasing()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = NewStore(db);

        var first = await store.AppendToolCallAsync(Entry("a"), Payload(), _ct);
        var second = await store.AppendToolCallAsync(Entry("b"), Payload(), _ct);

        Assert.True(second > first);
    }

    [Fact]
    public async Task PayloadStatsCountCallsAndCharacters()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = NewStore(db);

        var empty = await store.PayloadStatsAsync(_ct);
        Assert.Equal(0, empty.Calls);
        Assert.Equal(0, empty.Chars);

        await store.AppendToolCallAsync(Entry("a"), Payload("12345", "123"), _ct);
        await store.AppendToolCallAsync(Entry("b"), Payload("1", "1"), _ct);

        var stats = await store.PayloadStatsAsync(_ct);
        Assert.Equal(2, stats.Calls);
        Assert.Equal(10, stats.Chars);
    }

    [Fact]
    public async Task PayloadsCanBePrunedByAgeWithoutTouchingTheAuditRows()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = NewStore(db);
        await store.AppendToolCallAsync(Entry("old", minutes: -120), Payload(), _ct);
        await store.AppendToolCallAsync(Entry("new", minutes: 0), Payload(), _ct);

        var removed = await store.PrunePayloadsAsync(Now.AddMinutes(-60), _ct);

        Assert.Equal(1, removed);
        Assert.Equal(1, await db.CountAsync("audit_payloads", _ct));
        Assert.Equal(2, await db.CountAsync("audit_entries", _ct));
    }

    [Fact]
    public async Task ClearingPayloadsKeepsTheAuditRows()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = NewStore(db);
        await store.AppendToolCallAsync(Entry("a"), Payload(), _ct);
        await store.AppendToolCallAsync(Entry("b"), Payload(), _ct);

        var removed = await store.ClearPayloadsAsync(_ct);

        Assert.Equal(2, removed);
        Assert.Equal(0, await db.CountAsync("audit_payloads", _ct));
        Assert.Equal(2, await db.CountAsync("audit_entries", _ct));
    }

    [Fact]
    public async Task PruningOldEntriesAlsoRemovesTheirPayloads()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = NewStore(db);
        await store.AppendToolCallAsync(Entry("old", minutes: -120), Payload(), _ct);
        await store.AppendToolCallAsync(Entry("new", minutes: 0), Payload(), _ct);

        var removed = await store.PruneAsync(Now.AddMinutes(-60), _ct);

        Assert.Equal(1, removed);
        Assert.Equal(1, await db.CountAsync("audit_entries", _ct));
        Assert.Equal(1, await db.CountAsync("audit_payloads", _ct));
    }

    [Fact]
    public async Task UsageCountsOnlyToolCallsAndPrefersCountedTokens()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = NewStore(db);
        await store.AppendBatchAsync(
            [
                Entry("counted", bytes: 4000, tokens: 10),
                Entry("estimated", bytes: 5),
                Entry("nothing"),
                Entry("sign-in", AuditCategory.Auth, bytes: 4000, tokens: 999),
            ],
            _ct);

        var usage = await store.UsageAsync(null, _ct);

        Assert.Equal(3, usage.Calls);
        Assert.Equal(12, usage.Tokens);
    }

    [Fact]
    public async Task UsageCanStartAtAPointInTime()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = NewStore(db);
        await store.AppendBatchAsync([Entry("a", minutes: -10, tokens: 5), Entry("b", minutes: 10, tokens: 7)], _ct);

        var usage = await store.UsageAsync(Now, _ct);

        Assert.Equal(1, usage.Calls);
        Assert.Equal(7, usage.Tokens);
    }

    [Fact]
    public async Task UsageOfNothingIsZero()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);

        var usage = await NewStore(db).UsageAsync(null, _ct);

        Assert.Equal(0, usage.Calls);
        Assert.Equal(0, usage.Tokens);
    }

    [Fact]
    public async Task UsageByToolIsOrderedByTokensThenName()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = NewStore(db);
        await store.AppendBatchAsync(
            [Entry("b", tokens: 10), Entry("a", tokens: 10), Entry("c", tokens: 12), Entry("c", tokens: 8)],
            _ct);

        var rows = await store.UsageByToolAsync(null, 10, _ct);

        Assert.Equal(["c", "a", "b"], rows.Select(r => r.Tool).ToArray());
        Assert.Equal(2, rows[0].Usage.Calls);
        Assert.Equal(20, rows[0].Usage.Tokens);
        Assert.Single(await store.UsageByToolAsync(null, 1, _ct));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(51)]
    public async Task UsageByToolLimitIsBounded(int limit)
    {
        await using var db = await TestDatabase.CreateAsync(_ct);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => NewStore(db).UsageByToolAsync(null, limit, _ct));
    }

    [Fact]
    public async Task AuthAndApprovalEntriesAreStoredEvenThoughTheyAreAlsoShownLive()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var store = NewStore(db);

        await store.AppendAsync(Entry("sign-in", AuditCategory.Auth, AuditOutcome.Error, error: "bad"), _ct);
        await store.AppendAsync(Entry("approval.granted", AuditCategory.Approval, AuditOutcome.Denied), _ct);
        await store.AppendAsync(Entry("command.run", AuditCategory.Approval), _ct);

        Assert.Equal(3, await db.CountAsync("audit_entries", _ct));
    }
}
