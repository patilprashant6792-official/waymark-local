using Waymark.Core.Code;
using Waymark.Core.Text;

namespace Waymark.Tests.Text;

public sealed class MemberTermsTests
{
    private static HashSet<string> WordsOf(string? terms) =>
        (terms ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void TakesIdentifierPartsAndCommentWords()
    {
        var terms = MemberTerms.Extract("var token = refreshTokenStore.Rotate(userId); // invalidate the old session", 64);

        var words = WordsOf(terms);
        Assert.Contains("refresh", words);
        Assert.Contains("store", words);
        Assert.Contains("rotate", words);
        Assert.Contains("invalidate", words);
        Assert.Contains("session", words);
    }

    [Fact]
    public void LeavesOutKeywordsAndFillerWords()
    {
        var words = WordsOf(MemberTerms.Extract("public static void Run() { return null; }", 64));

        Assert.DoesNotContain("public", words);
        Assert.DoesNotContain("return", words);
        Assert.DoesNotContain("void", words);
        Assert.Contains("run", words);
    }

    [Fact]
    public void KeepsEachWordOnce()
    {
        var terms = MemberTerms.Extract("token token token Token", 64);

        Assert.Equal("token", terms);
    }

    [Fact]
    public void StopsAtTheCap()
    {
        var words = WordsOf(MemberTerms.Extract("alpha bravo charlie delta echo foxtrot", 3));

        Assert.Equal(3, words.Count);
    }

    [Fact]
    public void SkipsEncodedBlobs()
    {
        var blob = new string('a', 120) + "B1";

        var terms = MemberTerms.Extract("var x = \"" + blob + "\"; Visible();", 64);

        Assert.DoesNotContain(blob.ToLowerInvariant(), WordsOf(terms));
        Assert.Contains("visible", WordsOf(terms));
    }

    [Theory]
    [InlineData("var password = \"Zephyrquokka9876\";", "zephyrquokka9876")]
    [InlineData("log(\"password = Zephyrquokka9876\");", "zephyrquokka9876")]
    [InlineData("var j = \"{\\\"apiPassword\\\": \\\"Mirthwhistle8821\\\"}\";", "mirthwhistle8821")]
    [InlineData("const secret = 'Zephyrquokka9876';", "zephyrquokka9876")]
    [InlineData("Run(\"password = \\\"Zephyrquokka9876\\\"\");", "zephyrquokka9876")]
    public void HidesTheValueAfterASecretName(string source, string secret)
    {
        var words = WordsOf(MemberTerms.Extract(source + " Visible();", 64));

        Assert.DoesNotContain(secret, words);
        Assert.Contains("visible", words);
    }

    [Fact]
    public void KeepsTheSecretNameItself()
    {
        Assert.Contains("password", WordsOf(MemberTerms.Extract("var password = \"Zephyrquokka9876\";", 64)));
    }

    [Fact]
    public void ComparingToAStringIsNotMasked()
    {
        Assert.Contains("bearer", WordsOf(MemberTerms.Extract("if (tokenType == \"Bearer\") Run();", 64)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \n ")]
    public void NothingGivesNull(string? text)
    {
        Assert.Null(MemberTerms.Extract(text, 64));
    }

    [Fact]
    public void AnnotatesEachMemberFromItsOwnLines()
    {
        const string Source = "class A\n{\n    void First() { Alpha(); }\n    void Second() { Bravo(); }\n}\n";
        var file = FileWithMembers(new CodeMemberEntry(0, CodeMemberKind.Method, "First", "void First()", "void", null, null, null, 3, 3), new CodeMemberEntry(0, CodeMemberKind.Method, "Second", "void Second()", "void", null, null, null, 4, 4));

        var annotated = MemberTerms.Annotate(file, Source, new CodeIndexLimits(1000, 100, 100));

        Assert.Contains("alpha", WordsOf(annotated.Members[0].Terms));
        Assert.DoesNotContain("bravo", WordsOf(annotated.Members[0].Terms));
        Assert.Contains("bravo", WordsOf(annotated.Members[1].Terms));
    }

    [Fact]
    public void ReturnsTheSameFileWhenBodyTermsAreOff()
    {
        var file = FileWithMembers(new CodeMemberEntry(0, CodeMemberKind.Method, "First", "void First()", "void", null, null, null, 3, 3));

        var annotated = MemberTerms.Annotate(file, "a\nb\nvoid First() { Alpha(); }\n", new CodeIndexLimits(1000, 100, 100, IndexBodyTerms: false));

        Assert.Same(file, annotated);
    }

    [Fact]
    public void AMemberPastTheEndOfTheTextGetsNoTerms()
    {
        var file = FileWithMembers(new CodeMemberEntry(0, CodeMemberKind.Method, "Gone", "void Gone()", "void", null, null, null, 50, 60));

        var annotated = MemberTerms.Annotate(file, "one line", new CodeIndexLimits(1000, 100, 100));

        Assert.Null(annotated.Members[0].Terms);
    }

    private static CodeFileIndex FileWithMembers(params CodeMemberEntry[] members) =>
        new(
            new CodeFileStamp("A.cs", "hash", 10, DateTimeOffset.UnixEpoch),
            10,
            "source",
            [],
            [new CodeTypeEntry(CodeTypeKind.Class, "A", "A", null, null, null, null, null, 1, 5)],
            members,
            []);
}
