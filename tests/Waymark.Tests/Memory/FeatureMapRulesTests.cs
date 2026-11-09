using Waymark.Core.Memory;

namespace Waymark.Tests.Memory;

public sealed class FeatureMapRulesTests
{
    [Theory]
    [InlineData("order-flow", "order-flow")]
    [InlineData("Order Flow", "order-flow")]
    [InlineData("  My_Flow  ", "my-flow")]
    [InlineData("A", "a")]
    [InlineData("9lives", "9lives")]
    [InlineData("a--b", "a--b")]
    [InlineData("MIXED_case Name", "mixed-case-name")]
    public void ValidNamesAreNormalised(string text, string expected)
    {
        var accepted = FeatureMapRules.TryNormalizeName(text, out var name);

        Assert.True(accepted);
        Assert.Equal(expected, name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-leading")]
    [InlineData("_leading")]
    [InlineData("has!bang")]
    [InlineData("dot.name")]
    [InlineData("slash/name")]
    [InlineData("\u00e4bc")]
    public void InvalidNamesAreRefused(string? text)
    {
        var accepted = FeatureMapRules.TryNormalizeName(text, out var name);

        Assert.False(accepted);
        Assert.Null(name);
    }

    [Fact]
    public void NameLengthLimitIsSixtyCharacters()
    {
        Assert.True(FeatureMapRules.TryNormalizeName(new string('a', 60), out _));
        Assert.False(FeatureMapRules.TryNormalizeName(new string('a', 61), out _));
    }

    [Fact]
    public void LimitsAreTheDocumentedOnes()
    {
        Assert.Equal(30, FeatureMapRules.MaxComponentsPerMap);
        Assert.Equal(8, FeatureMapRules.MaxDepth);
        Assert.True(FeatureMapRules.SplitHintSteps < FeatureMapRules.MaxComponentsPerMap);
    }
}
