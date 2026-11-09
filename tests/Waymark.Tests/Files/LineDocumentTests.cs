using Waymark.Modules.Files;

namespace Waymark.Tests.Files;

public sealed class LineDocumentTests
{
    [Fact]
    public void AnEmptyFileHasNoLinesAndGetsAFinalLineBreakOnceFilled()
    {
        var document = LineDocument.Parse(string.Empty);

        Assert.Empty(document.Lines);
        Assert.True(document.EndsWithNewline);
        Assert.Equal(string.Empty, document.ToText());

        document.Lines.Add("x");
        Assert.Equal("x\n", document.ToText());
    }

    [Theory]
    [InlineData("a\nb\n", true)]
    [InlineData("a\nb", false)]
    [InlineData("a\r\nb\r\n", true)]
    [InlineData("a\r\nb", false)]
    public void TheFinalLineBreakIsRemembered(string text, bool endsWithNewline)
    {
        var document = LineDocument.Parse(text);

        Assert.Equal(["a", "b"], document.Lines);
        Assert.Equal(endsWithNewline, document.EndsWithNewline);
        Assert.Equal(endsWithNewline ? "a\nb\n" : "a\nb", document.ToText());
    }

    [Theory]
    [InlineData("\n", 1)]
    [InlineData("\n\n", 2)]
    [InlineData("a\n\n", 2)]
    public void BlankLinesAreLines(string text, int lineCount)
    {
        var document = LineDocument.Parse(text);

        Assert.Equal(lineCount, document.Lines.Count);
        Assert.Equal(text, document.ToText());
    }

    [Fact]
    public void ALoneCarriageReturnIsNotALineBreak() => Assert.Equal(["a\rb"], LineDocument.Parse("a\rb").Lines);

    [Fact]
    public void ParseRefusesNull() => Assert.Throws<ArgumentNullException>(() => LineDocument.Parse(null!));

    [Theory]
    [InlineData("a\nb\n", new[] { "a", "b" })]
    [InlineData("a\nb", new[] { "a", "b" })]
    [InlineData("a\r\nb", new[] { "a", "b" })]
    [InlineData("a\n\n", new[] { "a", "" })]
    [InlineData("", new[] { "" })]
    [InlineData("\n", new[] { "" })]
    public void PatchContentEndsItsLastLineWithAtMostOneFinalLineBreak(string content, string[] expected) =>
        Assert.Equal(expected, LineDocument.SplitContent(content));

    [Fact]
    public void SplitContentRefusesNull() => Assert.Throws<ArgumentNullException>(() => LineDocument.SplitContent(null!));
}
