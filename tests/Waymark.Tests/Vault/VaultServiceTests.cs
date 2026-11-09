using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Waymark.Core.Audit;
using Waymark.Core.Auth;
using Waymark.Data.Stores;
using Waymark.Host.Vault;
using Waymark.Tests.Support;

namespace Waymark.Tests.Vault;

public sealed class VaultServiceTests
{
    private static readonly DateTimeOffset Start = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);

    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private static VaultService CreateService(
        TestDatabase db,
        FakeAuditStore audit,
        IDataProtectionProvider? protection = null,
        CapturingLogger<VaultService>? logger = null) =>
        new(
            new VaultStore(db.Connections, db.Writes),
            protection ?? new EphemeralDataProtectionProvider(),
            audit,
            new StepClock(Start),
            (Microsoft.Extensions.Logging.ILogger<VaultService>?)logger ?? NullLogger<VaultService>.Instance);

    [Fact]
    public async Task SavingANewSecretReportsItAndAuditsItWithoutTheValue()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var audit = new FakeAuditStore();
        var service = CreateService(db, audit);

        var created = await service.SaveAsync("DB_PASS", "correct-horse-battery", _ct);

        Assert.True(created);
        var entry = Assert.Single(audit.Entries);
        Assert.Equal(AuditCategory.Config, entry.Category);
        Assert.Equal("vault.created", entry.Action);
        Assert.Equal(AuthDefaults.AdminActor, entry.Actor);
        Assert.Equal("DB_PASS", entry.Target);
        Assert.Null(entry.ArgsRedacted);
        Assert.DoesNotContain("correct-horse-battery", entry.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SavingAgainReplacesTheValue()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var audit = new FakeAuditStore();
        var service = CreateService(db, audit);
        await service.SaveAsync("DB_PASS", "first-value-123", _ct);

        var created = await service.SaveAsync("DB_PASS", "second-value-456", _ct);

        Assert.False(created);
        Assert.Equal(["vault.created", "vault.replaced"], audit.Entries.Select(e => e.Action).ToList());
        Assert.Equal("second-value-456", await service.ResolveAsync("DB_PASS", _ct));
    }

    [Fact]
    public async Task TheStoredValueIsEncrypted()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var service = CreateService(db, new FakeAuditStore());

        await service.SaveAsync("DB_PASS", "plain-text-secret", _ct);

        var stored = await db.ScalarTextAsync("SELECT protected_value FROM vault_secrets", _ct);
        Assert.NotNull(stored);
        Assert.DoesNotContain("plain-text-secret", stored, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheNameIsTrimmedAndTheListShowsOnlyAHint()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var service = CreateService(db, new FakeAuditStore());

        await service.SaveAsync("  DB_PASS  ", "abcdefghijkl", _ct);

        var listed = Assert.Single(await service.ListAsync(_ct));
        Assert.Equal("DB_PASS", listed.Name);
        Assert.Equal("ijkl", listed.Hint);
    }

    [Theory]
    [InlineData(null, "value")]
    [InlineData("", "value")]
    [InlineData("1BAD", "value")]
    [InlineData("GOOD", null)]
    [InlineData("GOOD", "")]
    public async Task AnInvalidNameOrValueIsRefusedAndNothingIsAudited(string? name, string? value)
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var audit = new FakeAuditStore();
        var service = CreateService(db, audit);

        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAsync(name, value, _ct));

        Assert.Empty(audit.Entries);
        Assert.Empty(await service.ListAsync(_ct));
    }

    [Fact]
    public async Task ResolveReturnsThePlainValueAndMarksTheSecretUsed()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var service = CreateService(db, new FakeAuditStore());
        await service.SaveAsync("DB_PASS", "correct-horse-battery", _ct);

        var value = await service.ResolveAsync("DB_PASS", _ct);

        Assert.Equal("correct-horse-battery", value);
        Assert.Equal(Start, Assert.Single(await service.ListAsync(_ct)).LastUsedUtc);
    }

    [Fact]
    public async Task ResolveReturnsNullForAnUnknownName()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var service = CreateService(db, new FakeAuditStore());

        Assert.Null(await service.ResolveAsync("NOPE", _ct));
    }

    [Fact]
    public async Task ASecretFromAnotherKeyRingCannotBeReadAndIsReportedNotThrown()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var writer = CreateService(db, new FakeAuditStore(), new EphemeralDataProtectionProvider());
        await writer.SaveAsync("DB_PASS", "correct-horse-battery", _ct);
        var logger = new CapturingLogger<VaultService>();
        var reader = CreateService(db, new FakeAuditStore(), new EphemeralDataProtectionProvider(), logger);

        var value = await reader.ResolveAsync("DB_PASS", _ct);

        Assert.Null(value);
        Assert.Contains(logger.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning);
        Assert.Null(Assert.Single(await reader.ListAsync(_ct)).LastUsedUtc);
    }

    [Fact]
    public async Task DeleteAuditsOnlyWhenSomethingWasRemoved()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var audit = new FakeAuditStore();
        var service = CreateService(db, audit);
        await service.SaveAsync("DB_PASS", "correct-horse-battery", _ct);
        audit.Entries.Clear();

        Assert.True(await service.DeleteAsync("DB_PASS", _ct));
        Assert.False(await service.DeleteAsync("DB_PASS", _ct));

        var entry = Assert.Single(audit.Entries);
        Assert.Equal("vault.deleted", entry.Action);
        Assert.Equal("DB_PASS", entry.Target);
    }

    [Fact]
    public async Task ScrubReplacesEverySavedValueInTheText()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var service = CreateService(db, new FakeAuditStore());
        await service.SaveAsync("DB_PASS", "correct-horse-battery", _ct);
        await service.SaveAsync("API_TOKEN", "another-long-value", _ct);

        var scrubbed = await service.ScrubAsync("pw=correct-horse-battery token=another-long-value ok", _ct);

        Assert.DoesNotContain("correct-horse-battery", scrubbed, StringComparison.Ordinal);
        Assert.DoesNotContain("another-long-value", scrubbed, StringComparison.Ordinal);
        Assert.Contains("DB_PASS", scrubbed, StringComparison.Ordinal);
        Assert.Contains("API_TOKEN", scrubbed, StringComparison.Ordinal);
        Assert.Contains("ok", scrubbed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScrubSkipsSecretsItCannotDecryptAndStillCleansTheRest()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var stale = CreateService(db, new FakeAuditStore(), new EphemeralDataProtectionProvider());
        await stale.SaveAsync("OLD_SECRET", "old-key-ring-value", _ct);
        var shared = new EphemeralDataProtectionProvider();
        var current = CreateService(db, new FakeAuditStore(), shared);
        await current.SaveAsync("NEW_SECRET", "new-key-ring-value", _ct);

        var scrubbed = await current.ScrubAsync("old-key-ring-value and new-key-ring-value", _ct);

        Assert.Contains("old-key-ring-value", scrubbed, StringComparison.Ordinal);
        Assert.DoesNotContain("new-key-ring-value", scrubbed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScrubOfNullIsRefused()
    {
        await using var db = await TestDatabase.CreateAsync(_ct);
        var service = CreateService(db, new FakeAuditStore());

        await Assert.ThrowsAsync<ArgumentNullException>(() => service.ScrubAsync(null!, _ct));
    }
}
