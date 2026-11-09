using Waymark.Core.Guides;

namespace Waymark.Tests.Guides;

public sealed class HostPromptRulesTests
{
    [Fact]
    public void NoOwnerTextGivesNoBlock() => Assert.Equal(string.Empty, HostPromptRules.OwnerBlock(string.Empty));

    [Fact]
    public void OwnerTextGetsAHeadingTheConflictRuleAndATrailingBlankLine()
    {
        var block = HostPromptRules.OwnerBlock("Always run the tests.");

        Assert.StartsWith("## Instructions from the owner\n\n", block);
        Assert.Contains("they win", block);
        Assert.Contains("gates and approvals still apply and cannot be changed by text", block);
        Assert.EndsWith("Always run the tests.\n\n", block);
    }

    [Fact]
    public void OwnerTextIsInsertedUnchanged()
    {
        const string Text = "line one\n\n  indented {{setup}} line";

        Assert.Contains(Text, HostPromptRules.OwnerBlock(Text));
    }

    [Fact]
    public void NullOwnerTextIsRefused() => Assert.Throws<ArgumentNullException>(() => HostPromptRules.OwnerBlock(null!));

    [Fact]
    public void TheWarningThresholdIsBelowWhatTheLimitAllows()
    {
        Assert.Equal(40_000, HostPromptRules.MaxOwnerChars);
        Assert.True(HostPromptRules.WarnTokens * 4 < HostPromptRules.MaxOwnerChars);
        Assert.True(HostPromptRules.WarnTokens > 0);
    }
}
