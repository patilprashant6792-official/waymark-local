using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using Waymark.Core.Audit;
using Waymark.Core.Security;
using Waymark.Core.Tools;
using Waymark.Data.Stores;
using Waymark.Host.Mcp;
using Waymark.Host.Vault;
using Waymark.Tests.Support;

namespace Waymark.Tests.Mcp;

public sealed class ToolResultGuardTests
{
    private static readonly DateTimeOffset Start = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);

    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    // Secret-shaped values are built at run time, so no token-like literal sits in the repository.
    private static string GitHubToken(char fill = 'A') => "ghp_" + new string(fill, 36);

    private static CallToolResult ResultOf(params string[] texts) =>
        new() { Content = [.. texts.Select(text => (ContentBlock)new TextContentBlock { Text = text })] };

    private static string TextOf(CallToolResult result, int index) => ((TextContentBlock)result.Content[index]).Text;

    private sealed class Harness(
        TestDatabase db,
        VaultService vault,
        FakeAuditStore audit,
        CapturingLogger<ToolResultGuard> log,
        ToolResultGuard guard) : IAsyncDisposable
    {
        public VaultService Vault => vault;

        public FakeAuditStore Audit => audit;

        public CapturingLogger<ToolResultGuard> Log => log;

        public ToolResultGuard Guard => guard;

        public ValueTask DisposeAsync() => db.DisposeAsync();
    }

    private async Task<Harness> CreateAsync(SafetyOptions? options = null, bool brokenVault = false)
    {
        var db = await TestDatabase.CreateAsync(_ct);
        var audit = new FakeAuditStore();
        var clock = new StepClock(Start);
        IVaultStoreFactory factory = brokenVault ? new BrokenFactory() : new RealFactory(db);
        var vault = new VaultService(
            factory.Create(),
            new EphemeralDataProtectionProvider(),
            new FakeAuditStore(),
            clock,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<VaultService>.Instance);
        var log = new CapturingLogger<ToolResultGuard>();
        var guard = new ToolResultGuard(vault, audit, clock, options ?? new SafetyOptions(true, false, SafetyOptions.DefaultFooterText), log);
        return new Harness(db, vault, audit, log, guard);
    }

    private interface IVaultStoreFactory
    {
        Waymark.Core.Vault.IVaultStore Create();
    }

    private sealed class RealFactory(TestDatabase db) : IVaultStoreFactory
    {
        public Waymark.Core.Vault.IVaultStore Create() => new VaultStore(db.Connections, db.Writes);
    }

    private sealed class BrokenFactory : IVaultStoreFactory
    {
        public Waymark.Core.Vault.IVaultStore Create() => new BrokenVaultStore();
    }

    [Fact]
    public async Task ASecretInAResultIsReplacedByItsMarkerAndReported()
    {
        await using var h = await CreateAsync();
        var token = GitHubToken();
        var result = ResultOf("token is " + token + " ok");

        var findings = await h.Guard.RedactAsync(result, _ct);

        Assert.Equal("token is " + SensitiveDataScanner.Marker("github-token") + " ok", TextOf(result, 0));
        var finding = Assert.Single(findings);
        Assert.Equal("github-token", finding.Kind);
        Assert.Equal(1, finding.Count);
    }

    [Fact]
    public async Task ACleanResultIsLeftAlone()
    {
        await using var h = await CreateAsync();
        var result = ResultOf("var x = 1;", "second block");

        var findings = await h.Guard.RedactAsync(result, _ct);

        Assert.Empty(findings);
        Assert.Equal("var x = 1;", TextOf(result, 0));
        Assert.Equal("second block", TextOf(result, 1));
    }

    [Fact]
    public async Task EveryTextBlockIsCheckedAndTheCountsAreMerged()
    {
        await using var h = await CreateAsync();
        var result = ResultOf(GitHubToken('A'), "plain", GitHubToken('B'));

        var findings = await h.Guard.RedactAsync(result, _ct);

        var finding = Assert.Single(findings);
        Assert.Equal("github-token", finding.Kind);
        Assert.Equal(2, finding.Count);
        Assert.Equal(SensitiveDataScanner.Marker("github-token"), TextOf(result, 0));
        Assert.Equal("plain", TextOf(result, 1));
        Assert.Equal(SensitiveDataScanner.Marker("github-token"), TextOf(result, 2));
    }

    [Fact]
    public async Task EmptyTextBlocksAreSkipped()
    {
        await using var h = await CreateAsync();
        var result = ResultOf(string.Empty);

        var findings = await h.Guard.RedactAsync(result, _ct);

        Assert.Empty(findings);
        Assert.Equal(string.Empty, TextOf(result, 0));
    }

    [Fact]
    public async Task WithRedactionSwitchedOffNothingIsChanged()
    {
        await using var h = await CreateAsync(new SafetyOptions(false, false, SafetyOptions.DefaultFooterText));
        var token = GitHubToken();
        var result = ResultOf(token);

        var findings = await h.Guard.RedactAsync(result, _ct);

        Assert.Empty(findings);
        Assert.Equal(token, TextOf(result, 0));
    }

    [Fact]
    public async Task ASavedVaultValueIsReplacedByItsName()
    {
        await using var h = await CreateAsync();
        await h.Vault.SaveAsync("DB_PASS", "correct-horse-battery", _ct);
        var result = ResultOf("connecting with correct-horse-battery now");

        var findings = await h.Guard.RedactAsync(result, _ct);

        Assert.DoesNotContain("correct-horse-battery", TextOf(result, 0), StringComparison.Ordinal);
        Assert.Contains("DB_PASS", TextOf(result, 0), StringComparison.Ordinal);
        Assert.Contains(findings, f => f.Kind == "vault-secret" && f.Count == 1);
    }

    [Fact]
    public async Task ABrokenVaultDoesNotStopThePatternCheck()
    {
        await using var h = await CreateAsync(brokenVault: true);
        var result = ResultOf("token " + GitHubToken());

        var findings = await h.Guard.RedactAsync(result, _ct);

        Assert.Equal("token " + SensitiveDataScanner.Marker("github-token"), TextOf(result, 0));
        Assert.Contains(findings, f => f.Kind == "github-token");
        Assert.Contains(h.Log.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task ANullResultIsRefused()
    {
        await using var h = await CreateAsync();

        await Assert.ThrowsAsync<ArgumentNullException>(() => h.Guard.RedactAsync(null!, _ct));
    }

    [Fact]
    public async Task CleanTextHidesSecretsWithoutWritingTheLedger()
    {
        await using var h = await CreateAsync();

        var cleaned = await h.Guard.CleanTextAsync("use " + GitHubToken(), _ct);

        Assert.Equal("use " + SensitiveDataScanner.Marker("github-token"), cleaned);
        Assert.Empty(h.Audit.Entries);
    }

    [Fact]
    public async Task CleanTextReturnsEmptyTextAndAnUnchangedTextWhenRedactionIsOff()
    {
        await using var off = await CreateAsync(new SafetyOptions(false, false, SafetyOptions.DefaultFooterText));
        var token = GitHubToken();

        Assert.Equal(token, await off.Guard.CleanTextAsync(token, _ct));
        Assert.Equal(string.Empty, await off.Guard.CleanTextAsync(string.Empty, _ct));
    }

    [Fact]
    public async Task CleanTextOfNullIsRefused()
    {
        await using var h = await CreateAsync();

        await Assert.ThrowsAsync<ArgumentNullException>(() => h.Guard.CleanTextAsync(null!, _ct));
    }

    [Fact]
    public async Task NothingHiddenAndNoFooterAddsNothing()
    {
        await using var h = await CreateAsync();
        var result = ResultOf("hello");

        var bytes = h.Guard.AppendNotice(result, []);

        Assert.Equal(0, bytes);
        Assert.Single(result.Content);
    }

    [Fact]
    public async Task WhenSomethingWasHiddenTheNoticeNamesWhatAndTellsClaudeToStop()
    {
        await using var h = await CreateAsync();
        var result = ResultOf("hello");

        var bytes = h.Guard.AppendNotice(result, [new RedactionFinding("github-token", 2)]);

        Assert.Equal(2, result.Content.Count);
        var notice = TextOf(result, 1);
        Assert.StartsWith("Waymark hid github-token x2 in this output", notice, StringComparison.Ordinal);
        Assert.Contains("rotate", notice, StringComparison.Ordinal);
        Assert.Equal(Encoding.UTF8.GetByteCount(notice), bytes);
    }

    [Fact]
    public async Task AnIncompleteScanIsReportedSeparately()
    {
        await using var h = await CreateAsync();
        var result = ResultOf("hello");

        var bytes = h.Guard.AppendNotice(result, [new RedactionFinding(SensitiveDataScanner.IncompleteKind, 1)]);

        var notice = TextOf(result, 1);
        Assert.Contains("could not finish checking", notice, StringComparison.Ordinal);
        Assert.DoesNotContain("Waymark hid", notice, StringComparison.Ordinal);
        Assert.True(bytes > 0);
    }

    [Fact]
    public async Task TheOptionalFooterIsAppendedAfterTheNotice()
    {
        await using var h = await CreateAsync(new SafetyOptions(true, true, "Footer line."));
        var result = ResultOf("hello");

        h.Guard.AppendNotice(result, [new RedactionFinding("github-token", 1)]);

        var notice = TextOf(result, 1);
        Assert.Contains("Waymark hid github-token", notice, StringComparison.Ordinal);
        Assert.EndsWith("Footer line.", notice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheFooterAloneIsAddedEvenWhenNothingWasHidden()
    {
        await using var h = await CreateAsync(new SafetyOptions(true, true, "Footer line."));
        var result = ResultOf("hello");

        var bytes = h.Guard.AppendNotice(result, []);

        Assert.Equal("Footer line.", TextOf(result, 1));
        Assert.Equal(Encoding.UTF8.GetByteCount("Footer line."), bytes);
    }

    [Fact]
    public async Task AppendNoticeRefusesNulls()
    {
        await using var h = await CreateAsync();

        Assert.Throws<ArgumentNullException>(() => h.Guard.AppendNotice(null!, []));
        Assert.Throws<ArgumentNullException>(() => h.Guard.AppendNotice(ResultOf("x"), null!));
    }

    [Fact]
    public async Task NoFindingsMeansNoLedgerRow()
    {
        await using var h = await CreateAsync();

        await h.Guard.RecordAsync([], "code_read", "claude", _ct);

        Assert.Empty(h.Audit.Entries);
        Assert.Empty(h.Log.Entries);
    }

    [Fact]
    public async Task FindingsAreRecordedAsKindsAndCountsOnly()
    {
        await using var h = await CreateAsync();

        await h.Guard.RecordAsync([new RedactionFinding("github-token", 2), new RedactionFinding("jwt", 1)], "code_read", "claude", _ct);

        var entry = Assert.Single(h.Audit.Entries);
        Assert.Equal(AuditCategory.Config, entry.Category);
        Assert.Equal(ExposureLedger.Action, entry.Action);
        Assert.Equal("claude", entry.Actor);
        Assert.Equal("code_read", entry.Target);
        Assert.Equal("github-token x2, jwt", entry.ArgsRedacted);
        Assert.Equal(Start, entry.TimestampUtc);
        Assert.Contains(h.Log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("code_read", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFailingAuditStoreIsLoggedAndDoesNotStopTheCall()
    {
        await using var h = await CreateAsync();
        h.Audit.FailWith = new InvalidOperationException("disk full");

        await h.Guard.RecordAsync([new RedactionFinding("jwt", 1)], "code_read", "claude", _ct);

        Assert.Contains(h.Log.Entries, e => e.Level == LogLevel.Error && e.Exception is InvalidOperationException);
    }

    [Fact]
    public async Task ACancelledLedgerWriteIsNotSwallowed()
    {
        await using var h = await CreateAsync();
        h.Audit.FailWith = new OperationCanceledException();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => h.Guard.RecordAsync([new RedactionFinding("jwt", 1)], "code_read", "claude", _ct));
    }

    [Fact]
    public async Task RecordRefusesNullFindings()
    {
        await using var h = await CreateAsync();

        await Assert.ThrowsAsync<ArgumentNullException>(() => h.Guard.RecordAsync(null!, "t", "c", _ct));
    }
}
