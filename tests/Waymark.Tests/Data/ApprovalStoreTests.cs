using Waymark.Core.Approvals;
using Waymark.Core.Auth;
using Waymark.Data.Stores;
using Waymark.Tests.Support;

namespace Waymark.Tests.Data;

public sealed class ApprovalStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 4, 5, 0, 0, TimeSpan.Zero);

    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private static NewApproval Request(byte seed, string clientId = "c1", DateTimeOffset? expires = null) =>
        new(
            [seed],
            "4821",
            clientId,
            "Claude",
            "http://localhost/cb",
            "localhost",
            "state-1",
            "challenge",
            "mcp",
            "http://localhost/mcp",
            Now,
            expires ?? Now.AddMinutes(10));

    private async Task<(TestDatabase Db, ApprovalStore Approvals, OAuthClientStore Clients)> CreateAsync(params string[] clientIds)
    {
        var db = await TestDatabase.CreateAsync(_ct);
        var clients = new OAuthClientStore(db.Connections, db.Writes);
        foreach (var id in clientIds.Length == 0 ? ["c1"] : clientIds)
        {
            await clients.AddAsync(new OAuthClient(id, "n", ["http://localhost/cb"], Now, null, null), _ct);
        }

        return (db, new ApprovalStore(db.Connections, db.Writes), clients);
    }

    [Fact]
    public async Task CreatesAPendingApproval()
    {
        var (db, approvals, _) = await CreateAsync();
        await using var _db = db;

        var created = await approvals.CreateAsync(Request(1), _ct);

        Assert.True(created.Id > 0);
        Assert.Equal(ApprovalStatus.Pending, created.Status);
        Assert.Equal("4821", created.PairingCode);
        Assert.Equal("c1", created.ClientId);
        Assert.Equal("state-1", created.State);
        Assert.Equal(Now, created.RequestedUtc);
        Assert.Equal(Now.AddMinutes(10), created.ExpiresUtc);
        Assert.Null(created.DecidedUtc);
        Assert.Null(created.CodeIssuedUtc);
        Assert.Equal(created, await approvals.GetAsync(created.Id, _ct));
    }

    [Fact]
    public async Task FindsAnApprovalByItsPollHash()
    {
        var (db, approvals, _) = await CreateAsync();
        await using var _db = db;
        var created = await approvals.CreateAsync(Request(7), _ct);

        Assert.Equal(created, await approvals.GetByPollHashAsync([7], _ct));
        Assert.Null(await approvals.GetByPollHashAsync([8], _ct));
        Assert.Null(await approvals.GetAsync(9999, _ct));
    }

    [Fact]
    public async Task APollHashCanOnlyBeUsedOnce()
    {
        var (db, approvals, _) = await CreateAsync();
        await using var _db = db;
        await approvals.CreateAsync(Request(1), _ct);

        await Assert.ThrowsAnyAsync<Exception>(() => approvals.CreateAsync(Request(1), _ct));
    }

    [Fact]
    public async Task ListPendingSkipsDecidedAndExpiredRequests()
    {
        var (db, approvals, _) = await CreateAsync();
        await using var _db = db;
        var open = await approvals.CreateAsync(Request(1), _ct);
        var decided = await approvals.CreateAsync(Request(2), _ct);
        await approvals.CreateAsync(Request(3, expires: Now.AddMinutes(1)), _ct);
        await approvals.DecideAsync(decided.Id, true, "owner", null, Now.AddSeconds(5), _ct);

        var pending = await approvals.ListPendingAsync(Now.AddMinutes(2), _ct);

        Assert.Equal([open.Id], pending.Select(a => a.Id).ToList());
    }

    [Fact]
    public async Task ListRecentIsNewestFirstAndLimited()
    {
        var (db, approvals, _) = await CreateAsync();
        await using var _db = db;
        var first = await approvals.CreateAsync(Request(1), _ct);
        var second = await approvals.CreateAsync(Request(2), _ct);
        var third = await approvals.CreateAsync(Request(3), _ct);

        var recent = await approvals.ListRecentAsync(2, _ct);

        Assert.Equal([third.Id, second.Id], recent.Select(a => a.Id).ToList());
        Assert.DoesNotContain(recent, a => a.Id == first.Id);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(501)]
    public async Task ListRecentRefusesALimitOutsideOneToFiveHundred(int limit)
    {
        var (db, approvals, _) = await CreateAsync();
        await using var _db = db;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => approvals.ListRecentAsync(limit, _ct));
    }

    [Fact]
    public async Task ApprovingRecordsWhoAndWhen()
    {
        var (db, approvals, _) = await CreateAsync();
        await using var _db = db;
        var created = await approvals.CreateAsync(Request(1), _ct);

        Assert.True(await approvals.DecideAsync(created.Id, true, "owner", "looks right", Now.AddMinutes(1), _ct));

        var decided = await approvals.GetAsync(created.Id, _ct);
        Assert.Equal(ApprovalStatus.Approved, decided!.Status);
        Assert.Equal("owner", decided.DecidedBy);
        Assert.Equal("looks right", decided.DecisionReason);
        Assert.Equal(Now.AddMinutes(1), decided.DecidedUtc);
    }

    [Fact]
    public async Task DenyingMarksItDenied()
    {
        var (db, approvals, _) = await CreateAsync();
        await using var _db = db;
        var created = await approvals.CreateAsync(Request(1), _ct);

        Assert.True(await approvals.DecideAsync(created.Id, false, "owner", null, Now.AddMinutes(1), _ct));

        Assert.Equal(ApprovalStatus.Denied, (await approvals.GetAsync(created.Id, _ct))!.Status);
    }

    [Fact]
    public async Task ARequestCanOnlyBeDecidedOnce()
    {
        var (db, approvals, _) = await CreateAsync();
        await using var _db = db;
        var created = await approvals.CreateAsync(Request(1), _ct);
        await approvals.DecideAsync(created.Id, false, "owner", null, Now.AddMinutes(1), _ct);

        Assert.False(await approvals.DecideAsync(created.Id, true, "owner", null, Now.AddMinutes(2), _ct));
        Assert.Equal(ApprovalStatus.Denied, (await approvals.GetAsync(created.Id, _ct))!.Status);
    }

    [Fact]
    public async Task AnExpiredRequestCannotBeDecided()
    {
        var (db, approvals, _) = await CreateAsync();
        await using var _db = db;
        var created = await approvals.CreateAsync(Request(1, expires: Now.AddMinutes(1)), _ct);

        Assert.False(await approvals.DecideAsync(created.Id, true, "owner", null, Now.AddMinutes(1), _ct));
        Assert.False(await approvals.DecideAsync(created.Id, true, "owner", null, Now.AddMinutes(5), _ct));
        Assert.Equal(ApprovalStatus.Pending, (await approvals.GetAsync(created.Id, _ct))!.Status);
    }

    [Fact]
    public async Task DecidingAnUnknownRequestReturnsFalse()
    {
        var (db, approvals, _) = await CreateAsync();
        await using var _db = db;

        Assert.False(await approvals.DecideAsync(9999, true, "owner", null, Now, _ct));
        await Assert.ThrowsAsync<ArgumentException>(() => approvals.DecideAsync(1, true, " ", null, Now, _ct));
    }

    [Fact]
    public async Task MintingForAnUnknownPollHashIsNotFound()
    {
        var (db, approvals, _) = await CreateAsync();
        await using var _db = db;

        var result = await approvals.TryMintCodeAsync([9], [1], Now.AddMinutes(5), Now, _ct);

        Assert.Equal(CodeMintStatus.NotFound, result.Status);
        Assert.Null(result.Approval);
    }

    [Fact]
    public async Task MintingWhileStillPendingReportsPending()
    {
        var (db, approvals, _) = await CreateAsync();
        await using var _db = db;
        await approvals.CreateAsync(Request(1), _ct);

        var result = await approvals.TryMintCodeAsync([1], [0xC0], Now.AddMinutes(5), Now.AddMinutes(1), _ct);

        Assert.Equal(CodeMintStatus.Pending, result.Status);
        Assert.Equal(0, await db.CountAsync("auth_codes", _ct));
    }

    [Fact]
    public async Task MintingAfterThePendingDeadlineExpiresTheRequest()
    {
        var (db, approvals, _) = await CreateAsync();
        await using var _db = db;
        var created = await approvals.CreateAsync(Request(1, expires: Now.AddMinutes(1)), _ct);

        var result = await approvals.TryMintCodeAsync([1], [0xC0], Now.AddMinutes(9), Now.AddMinutes(2), _ct);

        Assert.Equal(CodeMintStatus.Expired, result.Status);
        Assert.Equal(ApprovalStatus.Expired, result.Approval!.Status);
        var stored = await approvals.GetAsync(created.Id, _ct);
        Assert.Equal(ApprovalStatus.Expired, stored!.Status);
        Assert.Equal("timeout", stored.DecisionReason);
        Assert.Equal(0, await db.CountAsync("auth_codes", _ct));
    }

    [Fact]
    public async Task MintingForADeniedRequestIsDenied()
    {
        var (db, approvals, _) = await CreateAsync();
        await using var _db = db;
        var created = await approvals.CreateAsync(Request(1), _ct);
        await approvals.DecideAsync(created.Id, false, "owner", null, Now.AddMinutes(1), _ct);

        var result = await approvals.TryMintCodeAsync([1], [0xC0], Now.AddMinutes(9), Now.AddMinutes(2), _ct);

        Assert.Equal(CodeMintStatus.Denied, result.Status);
        Assert.Equal(0, await db.CountAsync("auth_codes", _ct));
    }

    [Fact]
    public async Task MintingForAnApprovedRequestIssuesTheCodeExactlyOnce()
    {
        var (db, approvals, _) = await CreateAsync();
        await using var _db = db;
        var created = await approvals.CreateAsync(Request(1), _ct);
        await approvals.DecideAsync(created.Id, true, "owner", null, Now.AddMinutes(1), _ct);
        var mintAt = Now.AddMinutes(2);

        var first = await approvals.TryMintCodeAsync([1], [0xC0], Now.AddMinutes(12), mintAt, _ct);
        var second = await approvals.TryMintCodeAsync([1], [0xC1], Now.AddMinutes(12), mintAt.AddSeconds(1), _ct);

        Assert.Equal(CodeMintStatus.Issued, first.Status);
        Assert.Equal(mintAt, first.Approval!.CodeIssuedUtc);
        Assert.Equal(CodeMintStatus.AlreadyIssued, second.Status);
        Assert.Equal(1, await db.CountAsync("auth_codes", _ct));
        Assert.Equal("C0", await db.ScalarTextAsync("SELECT hex(code_hash) FROM auth_codes", _ct));
        Assert.Equal(mintAt, (await approvals.GetAsync(created.Id, _ct))!.CodeIssuedUtc);
    }

    [Fact]
    public async Task TheIssuedCodeCopiesTheRequestsBindingDetails()
    {
        var (db, approvals, _) = await CreateAsync();
        await using var _db = db;
        var created = await approvals.CreateAsync(Request(1), _ct);
        await approvals.DecideAsync(created.Id, true, "owner", null, Now.AddMinutes(1), _ct);

        await approvals.TryMintCodeAsync([1], [0xC0], Now.AddMinutes(12), Now.AddMinutes(2), _ct);

        Assert.Equal("c1", await db.ScalarTextAsync("SELECT client_id FROM auth_codes", _ct));
        Assert.Equal("http://localhost/cb", await db.ScalarTextAsync("SELECT redirect_uri FROM auth_codes", _ct));
        Assert.Equal("challenge", await db.ScalarTextAsync("SELECT code_challenge FROM auth_codes", _ct));
        Assert.Equal("mcp", await db.ScalarTextAsync("SELECT scope FROM auth_codes", _ct));
        Assert.Equal("http://localhost/mcp", await db.ScalarTextAsync("SELECT resource FROM auth_codes", _ct));
    }

    [Fact]
    public async Task AnApprovalGivenJustBeforeTheDeadlineCanStillBeCollected()
    {
        var (db, approvals, _) = await CreateAsync();
        await using var _db = db;
        var created = await approvals.CreateAsync(Request(1), _ct);
        var decidedAt = Now.AddMinutes(10).AddSeconds(-10);
        await approvals.DecideAsync(created.Id, true, "owner", null, decidedAt, _ct);

        var late = await approvals.TryMintCodeAsync([1], [0xC0], Now.AddMinutes(20), Now.AddMinutes(10).AddSeconds(10), _ct);

        Assert.Equal(CodeMintStatus.Issued, late.Status);
    }

    [Fact]
    public async Task ApprovalsLeftUncollectedPastTheGraceWindowExpire()
    {
        var (db, approvals, _) = await CreateAsync();
        await using var _db = db;
        var created = await approvals.CreateAsync(Request(1), _ct);
        var decidedAt = Now.AddMinutes(10).AddSeconds(-10);
        await approvals.DecideAsync(created.Id, true, "owner", null, decidedAt, _ct);

        var tooLate = await approvals.TryMintCodeAsync([1], [0xC0], Now.AddMinutes(20), Now.AddMinutes(10).AddSeconds(30), _ct);

        Assert.Equal(CodeMintStatus.Expired, tooLate.Status);
        Assert.Equal(0, await db.CountAsync("auth_codes", _ct));
    }

    [Fact]
    public async Task ExpireStaleOnlyTouchesPendingRequestsPastTheirDeadline()
    {
        var (db, approvals, _) = await CreateAsync();
        await using var _db = db;
        var stale = await approvals.CreateAsync(Request(1, expires: Now.AddMinutes(1)), _ct);
        var fresh = await approvals.CreateAsync(Request(2), _ct);
        var decided = await approvals.CreateAsync(Request(3, expires: Now.AddMinutes(1)), _ct);
        await approvals.DecideAsync(decided.Id, true, "owner", null, Now.AddSeconds(5), _ct);

        var changed = await approvals.ExpireStaleAsync(Now.AddMinutes(2), _ct);

        Assert.Equal(1, changed);
        Assert.Equal(ApprovalStatus.Expired, (await approvals.GetAsync(stale.Id, _ct))!.Status);
        Assert.Equal("timeout", (await approvals.GetAsync(stale.Id, _ct))!.DecisionReason);
        Assert.Equal(ApprovalStatus.Pending, (await approvals.GetAsync(fresh.Id, _ct))!.Status);
        Assert.Equal(ApprovalStatus.Approved, (await approvals.GetAsync(decided.Id, _ct))!.Status);
    }

    [Fact]
    public async Task ExpireAllPendingEndsEveryWaitingRequestWithTheReason()
    {
        var (db, approvals, _) = await CreateAsync();
        await using var _db = db;
        var one = await approvals.CreateAsync(Request(1), _ct);
        var two = await approvals.CreateAsync(Request(2), _ct);
        var decided = await approvals.CreateAsync(Request(3), _ct);
        await approvals.DecideAsync(decided.Id, false, "owner", null, Now.AddSeconds(5), _ct);

        var changed = await approvals.ExpireAllPendingAsync(Now.AddMinutes(1), "server_restart", _ct);

        Assert.Equal(2, changed);
        Assert.Equal("server_restart", (await approvals.GetAsync(one.Id, _ct))!.DecisionReason);
        Assert.Equal(ApprovalStatus.Expired, (await approvals.GetAsync(two.Id, _ct))!.Status);
        Assert.Equal(ApprovalStatus.Denied, (await approvals.GetAsync(decided.Id, _ct))!.Status);
        await Assert.ThrowsAsync<ArgumentException>(() => approvals.ExpireAllPendingAsync(Now, " ", _ct));
    }

    [Fact]
    public async Task ClearFinishedRemovesFinishedRequestsAndKeepsLiveOnes()
    {
        var (db, approvals, _) = await CreateAsync();
        await using var _db = db;
        var denied = await approvals.CreateAsync(Request(1), _ct);
        await approvals.DecideAsync(denied.Id, false, "owner", null, Now.AddSeconds(1), _ct);
        var expired = await approvals.CreateAsync(Request(2, expires: Now.AddMinutes(1)), _ct);
        var waiting = await approvals.CreateAsync(Request(3), _ct);
        var connected = await approvals.CreateAsync(Request(4), _ct);
        await approvals.DecideAsync(connected.Id, true, "owner", null, Now.AddSeconds(1), _ct);

        var removed = await approvals.ClearFinishedAsync(Now.AddMinutes(2), _ct);

        Assert.Equal(2, removed);
        Assert.Null(await approvals.GetAsync(denied.Id, _ct));
        Assert.Null(await approvals.GetAsync(expired.Id, _ct));
        Assert.NotNull(await approvals.GetAsync(waiting.Id, _ct));
        Assert.NotNull(await approvals.GetAsync(connected.Id, _ct));
    }

    [Fact]
    public async Task ClearFinishedAlsoRemovesApprovedRequestsOfDisconnectedClientsAndTheirCodes()
    {
        var (db, approvals, clients) = await CreateAsync();
        await using var _db = db;
        var created = await approvals.CreateAsync(Request(1), _ct);
        await approvals.DecideAsync(created.Id, true, "owner", null, Now.AddSeconds(1), _ct);
        await approvals.TryMintCodeAsync([1], [0xC0], Now.AddMinutes(5), Now.AddSeconds(2), _ct);
        await clients.RevokeAsync("c1", Now.AddSeconds(3), _ct);

        var removed = await approvals.ClearFinishedAsync(Now.AddSeconds(4), _ct);

        Assert.Equal(1, removed);
        Assert.Equal(0, await db.CountAsync("approvals", _ct));
        Assert.Equal(0, await db.CountAsync("auth_codes", _ct));
    }

    [Fact]
    public async Task NullRequestsAreRefused()
    {
        var (db, approvals, _) = await CreateAsync();
        await using var _db = db;

        await Assert.ThrowsAsync<ArgumentNullException>(() => approvals.CreateAsync(null!, _ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => approvals.GetByPollHashAsync(null!, _ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => approvals.TryMintCodeAsync(null!, [1], Now, Now, _ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => approvals.TryMintCodeAsync([1], null!, Now, Now, _ct));
    }
}
