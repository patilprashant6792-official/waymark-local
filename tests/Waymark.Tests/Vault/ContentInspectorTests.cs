using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Waymark.Core.Security;
using Waymark.Data.Stores;
using Waymark.Host.Vault;
using Waymark.Tests.Support;

namespace Waymark.Tests.Vault;

public sealed class ContentInspectorTests
{
    private static readonly DateTimeOffset Start = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);

    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private static VaultService CreateVault(Waymark.Core.Vault.IVaultStore store) =>
        new(
            store,
            new EphemeralDataProtectionProvider(),
            new FakeAuditStore(),
            new StepClock(Start),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<VaultService>.Instance);

    [Fact]
    public async Task EmptyTextHasNoFindings()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var inspector = new ContentInspector(CreateVault(new VaultStore(db.Connections, db.Writes)), new CapturingLogger<ContentInspector>());

        Assert.Empty(await inspector.InspectAsync(string.Empty, _ct));
    }

    [Fact]
    public async Task OrdinaryTextHasNoFindings()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var inspector = new ContentInspector(CreateVault(new VaultStore(db.Connections, db.Writes)), new CapturingLogger<ContentInspector>());

        Assert.Empty(await inspector.InspectAsync("public static void Main() { }", _ct));
    }

    [Fact]
    public async Task ASavedVaultValueIsFound()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var vault = CreateVault(new VaultStore(db.Connections, db.Writes));
        await vault.SaveAsync("DB_PASS", "correct-horse-battery", _ct);
        var inspector = new ContentInspector(vault, new CapturingLogger<ContentInspector>());

        var findings = await inspector.InspectAsync("password is correct-horse-battery", _ct);

        var finding = Assert.Single(findings);
        Assert.Equal("vault-secret", finding.Kind);
        Assert.Equal(1, finding.Count);
    }

    [Fact]
    public async Task ACredentialShapeIsFound()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var inspector = new ContentInspector(CreateVault(new VaultStore(db.Connections, db.Writes)), new CapturingLogger<ContentInspector>());

        var findings = await inspector.InspectAsync("token " + "ghp_" + new string('A', 36), _ct);

        Assert.Contains(findings, f => f.Kind == "github-token");
    }

    [Fact]
    public async Task AFinishedRedactionMarkerIsFlaggedSoItIsNeverCopiedIntoAFile()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var inspector = new ContentInspector(CreateVault(new VaultStore(db.Connections, db.Writes)), new CapturingLogger<ContentInspector>());

        var findings = await inspector.InspectAsync("key = " + SensitiveDataScanner.Marker("github-token"), _ct);

        Assert.Contains(findings, f => f.Kind == "redaction-marker");
    }

    [Fact]
    public async Task ABrokenVaultDoesNotStopThePatternCheck()
    {
        var logger = new CapturingLogger<ContentInspector>();
        var inspector = new ContentInspector(CreateVault(new BrokenVaultStore()), logger);

        var findings = await inspector.InspectAsync("token " + "ghp_" + new string('A', 36), _ct);

        Assert.Contains(findings, f => f.Kind == "github-token");
        Assert.DoesNotContain(findings, f => f.Kind == "vault-secret");
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task NullTextIsRefused()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var inspector = new ContentInspector(CreateVault(new VaultStore(db.Connections, db.Writes)), new CapturingLogger<ContentInspector>());

        await Assert.ThrowsAsync<ArgumentNullException>(() => inspector.InspectAsync(null!, _ct));
    }
}
