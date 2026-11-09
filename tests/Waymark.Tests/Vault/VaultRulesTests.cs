using Waymark.Core.Vault;

namespace Waymark.Tests.Vault;

public sealed class VaultRulesTests
{
    [Theory]
    [InlineData("GITHUB_TOKEN")]
    [InlineData("a")]
    [InlineData("Db_Password_2")]
    [InlineData("  PADDED  ")]
    public void ValidNameAndValueAreAccepted(string name)
    {
        var accepted = VaultRules.TryNormalize(name, "a value", out var normalized, out var error);

        Assert.True(accepted, error);
        Assert.Equal(name.Trim(), normalized);
        Assert.Null(error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1ABC")]
    [InlineData("_ABC")]
    [InlineData("A-B")]
    [InlineData("A B")]
    [InlineData("A.B")]
    public void InvalidNameIsRefusedWithTheNamingRule(string? name)
    {
        var accepted = VaultRules.TryNormalize(name, "a value", out _, out var error);

        Assert.False(accepted);
        Assert.NotNull(error);
        Assert.Contains("Use letters, digits and underscores", error);
    }

    [Fact]
    public void NameLengthLimitIsInclusive()
    {
        Assert.True(VaultRules.TryNormalize(new string('A', VaultRules.MaxNameLength), "v", out _, out _));
        Assert.False(VaultRules.TryNormalize(new string('A', VaultRules.MaxNameLength + 1), "v", out _, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void EmptyValueIsRefused(string? value)
    {
        var accepted = VaultRules.TryNormalize("NAME", value, out _, out var error);

        Assert.False(accepted);
        Assert.NotNull(error);
    }

    [Fact]
    public void ValueLengthLimitIsInclusive()
    {
        Assert.True(VaultRules.TryNormalize("NAME", new string('x', VaultRules.MaxValueLength), out _, out _));
        Assert.False(VaultRules.TryNormalize("NAME", new string('x', VaultRules.MaxValueLength + 1), out _, out _));
    }

    [Theory]
    [InlineData("short", "")]
    [InlineData("12345678", "")]
    [InlineData("123456789", "6789")]
    [InlineData("abcdefghijkl", "ijkl")]
    [InlineData("", "")]
    public void HintIsTheLastFourCharactersOfALongEnoughValue(string value, string expected) =>
        Assert.Equal(expected, VaultRules.HintOf(value));

    [Fact]
    public void ScrubReplacesAValueWithItsName()
    {
        var secrets = new Dictionary<string, string> { ["GITHUB_TOKEN"] = "ghp-value-123" };

        var result = VaultRules.Scrub("curl token ghp-value-123 again ghp-value-123", secrets);

        Assert.DoesNotContain("ghp-value-123", result);
        Assert.Contains("GITHUB_TOKEN", result);
    }

    [Fact]
    public void ScrubReplacesTheLongerValueFirstSoAContainedValueDoesNotSplitIt()
    {
        var secrets = new Dictionary<string, string>
        {
            ["SHORT"] = "abcdef",
            ["LONG"] = "abcdefghij",
        };

        var result = VaultRules.Scrub("x abcdefghij y abcdef z", secrets);

        Assert.DoesNotContain("abcdef", result);
        Assert.Contains("LONG", result);
        Assert.Contains("SHORT", result);
    }

    [Fact]
    public void ScrubLeavesValuesShorterThanTheMinimumAlone()
    {
        var secrets = new Dictionary<string, string> { ["PIN"] = "12345" };

        Assert.Equal("code 12345", VaultRules.Scrub("code 12345", secrets));
    }

    [Fact]
    public void ScrubIsCaseSensitive()
    {
        var secrets = new Dictionary<string, string> { ["K"] = "SecretValue" };

        Assert.Equal("secretvalue", VaultRules.Scrub("secretvalue", secrets));
    }

    [Fact]
    public void ScrubWithoutSecretsReturnsTheTextAsItIs()
    {
        Assert.Equal("plain text", VaultRules.Scrub("plain text", new Dictionary<string, string>()));
        Assert.Equal(string.Empty, VaultRules.Scrub(string.Empty, new Dictionary<string, string> { ["K"] = "SecretValue" }));
    }
}
