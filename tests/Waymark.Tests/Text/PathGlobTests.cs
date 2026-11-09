using Waymark.Core.Text;

namespace Waymark.Tests.Text;

public sealed class PathGlobTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("./")]
    [InlineData("/")]
    public void NoUsablePatternGivesNull(string? pattern) => Assert.Null(PathGlob.Parse(pattern));

    [Theory]
    [InlineData("*.cs", "Program.cs", true)]
    [InlineData("*.cs", "src/deep/Program.cs", true)]
    [InlineData("*.cs", "src/Program.csproj", false)]
    [InlineData("*.CS", "a.cs", true)]
    [InlineData("Program.cs", "src/Program.cs", true)]
    [InlineData("Program.cs", "src/MyProgram.cs", false)]
    public void PatternWithoutASlashMatchesTheFileNameInAnyFolder(string pattern, string path, bool expected) =>
        Assert.Equal(expected, PathGlob.Parse(pattern)!.IsMatch(path));

    [Theory]
    [InlineData("src/*.cs", "src/a.cs", true)]
    [InlineData("src/*.cs", "src/sub/a.cs", false)]
    [InlineData("src/*.cs", "other/src/a.cs", false)]
    [InlineData("SRC/*.cs", "src/a.cs", true)]
    public void SingleStarStaysInsideOneFolder(string pattern, string path, bool expected) =>
        Assert.Equal(expected, PathGlob.Parse(pattern)!.IsMatch(path));

    [Theory]
    [InlineData("**/a.cs", "a.cs", true)]
    [InlineData("**/a.cs", "x/y/a.cs", true)]
    [InlineData("**/a.cs", "x/b.cs", false)]
    [InlineData("src/**/a.cs", "src/a.cs", true)]
    [InlineData("src/**/a.cs", "src/x/y/a.cs", true)]
    [InlineData("src/**/a.cs", "lib/a.cs", false)]
    [InlineData("src/**", "src/x/y/z.txt", true)]
    public void DoubleStarCrossesFolders(string pattern, string path, bool expected) =>
        Assert.Equal(expected, PathGlob.Parse(pattern)!.IsMatch(path));

    [Theory]
    [InlineData("docs/", "docs/a.md", true)]
    [InlineData("docs/", "docs/x/y/a.md", true)]
    [InlineData("docs/", "other/a.md", false)]
    [InlineData("docs/", "docsx/a.md", false)]
    public void TrailingSlashMeansEverythingBelowTheFolder(string pattern, string path, bool expected) =>
        Assert.Equal(expected, PathGlob.Parse(pattern)!.IsMatch(path));

    [Theory]
    [InlineData("a?.cs", "ab.cs", true)]
    [InlineData("a?.cs", "a.cs", false)]
    [InlineData("a?.cs", "abc.cs", false)]
    [InlineData("src/a?.cs", "src/a/.cs", false)]
    public void QuestionMarkIsExactlyOneNonSeparatorCharacter(string pattern, string path, bool expected) =>
        Assert.Equal(expected, PathGlob.Parse(pattern)!.IsMatch(path));

    [Theory]
    [InlineData("a+b.cs", "a+b.cs", true)]
    [InlineData("a+b.cs", "aab.cs", false)]
    [InlineData("(x).cs", "(x).cs", true)]
    [InlineData("a.cs", "aXcs", false)]
    public void RegexCharactersAreTakenLiterally(string pattern, string path, bool expected) =>
        Assert.Equal(expected, PathGlob.Parse(pattern)!.IsMatch(path));

    [Fact]
    public void BackslashesAndLeadingDotSlashAreNormalised()
    {
        Assert.True(PathGlob.Parse(@"src\*.cs")!.IsMatch("src/a.cs"));
        Assert.True(PathGlob.Parse("./src/*.cs")!.IsMatch("src/a.cs"));
        Assert.True(PathGlob.Parse("/src/*.cs")!.IsMatch("src/a.cs"));
        Assert.True(PathGlob.Parse("  src/*.cs  ")!.IsMatch("src/a.cs"));
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("a/../b")]
    [InlineData("..")]
    [InlineData("C:/x")]
    [InlineData("a:b")]
    public void PatternsThatLeaveTheWorkspaceAreRefused(string pattern) =>
        Assert.Throws<ArgumentException>(() => PathGlob.Parse(pattern));

    [Fact]
    public void PatternLengthLimitIsInclusive()
    {
        Assert.NotNull(PathGlob.Parse(new string('a', 200)));
        Assert.Throws<ArgumentException>(() => PathGlob.Parse(new string('a', 201)));
    }

    [Fact]
    public void DotsInNamesAreNotTreatedAsParentReferences() =>
        Assert.True(PathGlob.Parse("a..b.cs")!.IsMatch("a..b.cs"));

    [Fact]
    public void NullPathIsRefused() =>
        Assert.Throws<ArgumentNullException>(() => PathGlob.Parse("*.cs")!.IsMatch(null!));

    [Fact]
    public void HostilePatternDoesNotHangOrThrow()
    {
        var glob = PathGlob.Parse(string.Concat(Enumerable.Repeat("**/a*", 20)) + "b")!;

        var result = glob.IsMatch(string.Concat(Enumerable.Repeat("a/", 60)));

        Assert.False(result);
    }
}
