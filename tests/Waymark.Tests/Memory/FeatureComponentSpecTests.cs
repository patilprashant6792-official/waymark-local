using Waymark.Core.Memory;

namespace Waymark.Tests.Memory;

public sealed class FeatureComponentSpecTests
{
    private static FeatureComponent Parse(string spec)
    {
        Assert.True(FeatureComponentSpec.TryParse(spec, out var component, out var error), error);
        return component!;
    }

    private static string Error(string? spec)
    {
        Assert.False(FeatureComponentSpec.TryParse(spec, out var component, out var error));
        Assert.Null(component);
        Assert.False(string.IsNullOrEmpty(error));
        return error!;
    }

    [Fact]
    public void AFileIsJustAPath()
    {
        var component = Parse("src/Orders/Order.cs");

        Assert.Equal("src/Orders/Order.cs", component.Path);
        Assert.True(component.IsFile);
        Assert.False(component.IsRange);
        Assert.Null(component.Type);
        Assert.Null(component.Member);
    }

    [Fact]
    public void BackslashesBecomeForwardSlashesAndTheTextIsTrimmed() =>
        Assert.Equal("src/A.cs", Parse("  src\\A.cs  ").Path);

    [Fact]
    public void ANameWithoutADotIsAMember()
    {
        var component = Parse("src/A.cs:Place");

        Assert.Null(component.Type);
        Assert.Equal("Place", component.Member);
        Assert.False(component.IsFile);
    }

    [Fact]
    public void TypeDotMemberIsSplitAtTheLastDot()
    {
        var simple = Parse("src/A.cs:Order.Place");
        var nested = Parse("src/A.cs:Outer.Inner.Method");

        Assert.Equal("Order", simple.Type);
        Assert.Equal("Place", simple.Member);
        Assert.Equal("Outer.Inner", nested.Type);
        Assert.Equal("Method", nested.Member);
    }

    [Theory]
    [InlineData("src/A.cs#L10-40", 10, 40)]
    [InlineData("src/A.cs#10-40", 10, 40)]
    [InlineData("src/A.cs#L5", 5, 5)]
    [InlineData("src/A.cs#L7-L9", 7, 9)]
    [InlineData("src/A.cs#L1-400", 1, 400)]
    public void LineRangesAreRead(string spec, int start, int end)
    {
        var component = Parse(spec);

        Assert.True(component.IsRange);
        Assert.Equal("src/A.cs", component.Path);
        Assert.Equal(start, component.StartLine);
        Assert.Equal(end, component.EndLine);
    }

    [Theory]
    [InlineData("src/A.cs#L0")]
    [InlineData("src/A.cs#L40-10")]
    [InlineData("src/A.cs#L1-401")]
    [InlineData("src/A.cs#Lx")]
    [InlineData("src/A.cs#")]
    [InlineData("src/A.cs#L1-2-3")]
    [InlineData("src/A.cs#L12345678")]
    [InlineData("#L1-2")]
    [InlineData("src/A.cs:X#L1-2")]
    public void BadLineRangesAreRefused(string spec) => Error(spec);

    [Theory]
    [InlineData("src/A.cs:")]
    [InlineData("src/A.cs:.Member")]
    [InlineData("src/A.cs:Type.")]
    public void ASymbolNeedsANameAfterTheColon(string spec) =>
        Assert.Contains("After ':'", Error(spec));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyIsRefused(string? spec) => Assert.Contains("1 to 300", Error(spec));

    [Fact]
    public void LengthLimitIsInclusive()
    {
        var longest = new string('a', FeatureComponentSpec.MaxSpecChars);

        Assert.True(FeatureComponentSpec.TryParse(longest, out _, out _));
        Assert.Contains("1 to 300", Error(longest + "a"));
    }

    [Theory]
    [InlineData("src/A B.cs")]
    [InlineData("a.cs,b.cs")]
    [InlineData("a.cs\tb")]
    public void SpacesAndCommasAreRefused(string spec) => Assert.Contains("one component per line", Error(spec));

    [Fact]
    public void AbsolutePathsAreRefused() => Assert.Contains("absolute", Error("/etc/passwd"));

    [Theory]
    [InlineData("../outside.cs")]
    [InlineData("a/../b.cs")]
    [InlineData("./a.cs")]
    [InlineData("a//b.cs")]
    [InlineData("a/b/")]
    [InlineData(":Name")]
    public void PathsThatAreNotInsideTheWorkspaceAreRefused(string spec) => Assert.Contains("Not a path inside the workspace", Error(spec));

    [Fact]
    public void LongTextInAnErrorIsShortened()
    {
        var error = Error(new string('x', 100) + " y");

        Assert.Contains(new string('x', 60) + "...", error);
        Assert.DoesNotContain(new string('x', 61), error);
    }

    [Theory]
    [InlineData("src/A.cs")]
    [InlineData("src/A.cs:Place")]
    [InlineData("src/A.cs:Order.Place")]
    [InlineData("src/A.cs#L10-40")]
    public void FormatPrintsTheCanonicalTextAndParsesBack(string spec)
    {
        var component = Parse(spec);

        Assert.Equal(spec, FeatureComponentSpec.Format(component));
        Assert.Equal(component, Parse(FeatureComponentSpec.Format(component)));
    }

    [Fact]
    public void ASingleLineRangeIsPrintedAsARange() =>
        Assert.Equal("src/A.cs#L5-5", FeatureComponentSpec.Format(Parse("src/A.cs#L5")));

    [Fact]
    public void FormatRefusesNull() => Assert.Throws<ArgumentNullException>(() => FeatureComponentSpec.Format(null!));

    [Fact]
    public void ALineWithoutANoteHasAnEmptyNote()
    {
        Assert.True(FeatureComponentSpec.TryParseLine("src/A.cs:Place", out var line, out var error), error);

        Assert.Equal(string.Empty, line!.Note);
        Assert.Equal("src/A.cs:Place", FeatureComponentSpec.Format(line.Component));
    }

    [Theory]
    [InlineData("src/A.cs | the entry", "the entry")]
    [InlineData("- src/A.cs|the entry", "the entry")]
    [InlineData("* `src/A.cs` | the entry", "the entry")]
    [InlineData("src/A.cs | first | second", "first | second")]
    [InlineData("src/A.cs |", "")]
    public void ALineWithANoteIsSplitAtTheFirstBar(string text, string note)
    {
        Assert.True(FeatureComponentSpec.TryParseLine(text, out var line, out var error), error);

        Assert.Equal("src/A.cs", line!.Component.Path);
        Assert.Equal(note, line.Note);
    }

    [Fact]
    public void NoteLengthLimitIsInclusive()
    {
        var longest = new string('n', FeatureComponentSpec.MaxNoteChars);

        Assert.True(FeatureComponentSpec.TryParseLine("a.cs | " + longest, out _, out _));
        Assert.False(FeatureComponentSpec.TryParseLine("a.cs | " + longest + "n", out var line, out var error));
        Assert.Null(line);
        Assert.Contains("longer than 120", error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("two words")]
    public void ABadLineIsRefused(string? text)
    {
        Assert.False(FeatureComponentSpec.TryParseLine(text, out var line, out var error));
        Assert.Null(line);
        Assert.NotNull(error);
    }
}
