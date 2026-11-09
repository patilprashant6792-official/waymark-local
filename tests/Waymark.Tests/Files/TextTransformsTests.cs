using Waymark.Modules.Files;

namespace Waymark.Tests.Files;

public sealed class TextTransformsTests
{
    private static string Apply(string text, List<ReplaceRule> rules, out List<string> notes, int maxRules = 20)
    {
        notes = [];
        return TextTransforms.ApplyReplacements(text, rules, maxRules, notes);
    }

    private static string Fail(string text, List<ReplaceRule?> rules, int maxRules = 20)
    {
        var notes = new List<string>();
        var exception = Assert.Throws<PatchValidationException>(
            () => TextTransforms.ApplyReplacements(text, [.. rules.Select(r => r!)], maxRules, notes));
        Assert.Empty(notes);
        return exception.Message;
    }

    [Fact]
    public void EveryMatchIsReplacedAndTheTotalIsNoted()
    {
        var result = Apply("foo foo bar", [new ReplaceRule("foo", "x", 2), new ReplaceRule("bar", "y")], out var notes);

        Assert.Equal("x x y", result);
        Assert.Equal("3 replacement(s)", Assert.Single(notes));
    }

    [Fact]
    public void EachRuleSeesTheTextLeftByThePreviousOne()
    {
        var result = Apply("a", [new ReplaceRule("a", "b"), new ReplaceRule("b", "c")], out _);

        Assert.Equal("c", result);
    }

    [Fact]
    public void AnEmptyReplacementDeletesTheText() =>
        Assert.Equal("ac", Apply("abc", [new ReplaceRule("b", string.Empty)], out _));

    [Fact]
    public void MatchingIsLiteralAndCaseSensitive()
    {
        Assert.Equal("a-b", Apply("a.b", [new ReplaceRule(".", "-")], out _));
        Assert.Contains("found 0 match(es)", Fail("Abc", [new ReplaceRule("a", "x")]));
    }

    [Fact]
    public void WindowsLineBreaksInTheRuleMatchTheNormalisedText() =>
        Assert.Equal("x\ny", Apply("a\nb", [new ReplaceRule("a\r\nb", "x\r\ny")], out _));

    [Fact]
    public void AWrongMatchCountFailsTheWholeCopy() =>
        Assert.Equal(
            "replace #1: found 2 match(es) of 'foo', expected 1. Nothing was copied.",
            Fail("foo foo", [new ReplaceRule("foo", "x", 1)]));

    [Fact]
    public void WithoutACountAtLeastOneMatchIsRequired() =>
        Assert.Equal(
            "replace #2: found 0 match(es) of 'zzz', expected at least 1. Nothing was copied.",
            Fail("foo", [new ReplaceRule("foo", "x"), new ReplaceRule("zzz", "y")]));

    [Fact]
    public void LongOrMultiLineTextIsShortenedInTheMessage()
    {
        var multiLine = Fail("x", [new ReplaceRule("a\nb", "c")]);
        var longText = Fail("x", [new ReplaceRule(new string('q', 100), "c")]);

        Assert.Contains("'a b'", multiLine);
        Assert.Contains("'" + new string('q', 60) + "...'", longText);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void AnEmptyFindIsRefused(string? find) =>
        Assert.Equal("replace #1: find must not be empty.", Fail("abc", [new ReplaceRule(find, "x")]));

    [Fact]
    public void AMissingWithIsRefused() =>
        Assert.Equal(
            "replace #1: with is required (use an empty string to delete the text).",
            Fail("abc", [new ReplaceRule("a", null)]));

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void ACountBelowOneIsRefused(int count) =>
        Assert.Equal("replace #1: count must be at least 1 (or leave it out).", Fail("abc", [new ReplaceRule("a", "x", count)]));

    [Fact]
    public void ANullRuleIsRefused() => Assert.Equal("replace #1 is empty.", Fail("abc", [null]));

    [Fact]
    public void TooManyRulesAreRefused() =>
        Assert.Equal(
            "At most 2 replace rules can be used for one copy.",
            Fail("abc", [new ReplaceRule("a", "1"), new ReplaceRule("b", "2"), new ReplaceRule("c", "3")], maxRules: 2));

    [Fact]
    public void ShiftingByZeroChangesNothing() => Assert.Equal("  a\n b", TextTransforms.ShiftIndent("  a\n b", 0));

    [Fact]
    public void ASpacesShiftIndentsOnlyLinesThatHaveText() =>
        Assert.Equal("  a\n\n    b\n   \n", TextTransforms.ShiftIndent("a\n\n  b\n   \n", 2));

    [Fact]
    public void ANegativeShiftRemovesUpToThatManySpacesOrTabs()
    {
        Assert.Equal("  a\nb\nc\nd", TextTransforms.ShiftIndent("    a\n\tb\n  c\nd", -2));
        Assert.Equal(" x", TextTransforms.ShiftIndent("\t\t x", -2));
    }

    [Theory]
    [InlineData(64)]
    [InlineData(-64)]
    public void TheLargestShiftIsSixtyFour(int indent) => Assert.NotNull(TextTransforms.ShiftIndent("a", indent));

    [Theory]
    [InlineData(65)]
    [InlineData(-65)]
    public void ALargerShiftIsRefused(int indent)
    {
        var exception = Assert.Throws<PatchValidationException>(() => TextTransforms.ShiftIndent("a", indent));

        Assert.Equal("indent must be between -64 and 64.", exception.Message);
    }
}
