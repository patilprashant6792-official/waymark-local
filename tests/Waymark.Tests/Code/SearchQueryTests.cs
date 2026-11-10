using Waymark.Modules.Code.Search;

namespace Waymark.Tests.Code;

public sealed class SearchQueryTests
{
    private static SearchQuery Parse(string? query, IReadOnlyList<string>? terms = null, int maxLength = 200, int maxWords = 16) =>
        SearchQuery.Parse(query, terms, maxLength, maxWords);

    [Fact]
    public void OneWordIsAnIdentifierLookup()
    {
        var query = Parse("UserService");

        Assert.True(query.IsIdentifier);
        Assert.Equal(["UserService"], query.Words);
    }

    [Fact]
    public void SeveralWordsAreADescription()
    {
        var query = Parse("refresh token rotation");

        Assert.False(query.IsIdentifier);
        Assert.Equal(["refresh", "token", "rotation"], query.Words);
    }

    [Fact]
    public void ReadsPhrasesRequiredAndExcludedWords()
    {
        var query = Parse("\"refresh token\" +rotate -legacy");

        Assert.Single(query.Phrases);
        Assert.Equal(["refresh", "token"], query.Phrases[0]);
        Assert.Equal(["rotate"], query.Required);
        Assert.Equal(["legacy"], query.Excluded);
        Assert.Contains("refresh", query.Words);
        Assert.Contains("rotate", query.Words);
        Assert.DoesNotContain("legacy", query.Words);
    }

    [Fact]
    public void DropsTheFillerWordsOfASentence()
    {
        var query = Parse("how does the user login work");

        Assert.Equal(["user", "login", "work"], query.Words);
    }

    [Fact]
    public void KeepsFillerWordsWhenNothingElseIsLeft()
    {
        Assert.Equal(["the"], Parse("the").Words);
    }

    [Fact]
    public void PhrasesBecomeRequiredWordsWhenAsked()
    {
        var query = Parse("\"refresh token\" store").PhrasesAsRequired();

        Assert.Empty(query.Phrases);
        Assert.Equal(["refresh", "token"], query.Required);
    }

    [Fact]
    public void AddsTermsThatTheQueryDoesNotHave()
    {
        var query = Parse("token", ["jwt", "Token", "bearer"]);

        Assert.Equal(["jwt", "bearer"], query.Terms);
        Assert.False(query.IsIdentifier);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-legacy")]
    [InlineData("+")]
    [InlineData("how does")]
    public void RefusesAQueryWithNothingToSearchFor(string? text)
    {
        Assert.Throws<ArgumentException>(() => Parse(text));
    }

    [Fact]
    public void RefusesAQueryThatIsTooLong()
    {
        Assert.Throws<ArgumentException>(() => Parse(new string('a', 30), maxLength: 20));
    }

    [Fact]
    public void RefusesTooManyWordsAndTerms()
    {
        Assert.Throws<ArgumentException>(() => Parse("one two", ["three"], maxWords: 2));
    }

    [Fact]
    public void ALoneSignIsIgnored()
    {
        Assert.Equal(["token"], Parse("- token").Words);
    }

    [Fact]
    public void AnUnclosedQuoteTakesTheRestAsAPhrase()
    {
        var query = Parse("\"refresh token");

        Assert.Single(query.Phrases);
    }

    [Theory]
    [InlineData("names", true, "Names")]
    [InlineData("CODE", true, "Code")]
    [InlineData("docs", true, "Docs")]
    [InlineData("", true, "All")]
    [InlineData(null, true, "All")]
    [InlineData("everything", false, "All")]
    public void ParsesMatchIn(string? text, bool valid, string expected)
    {
        var ok = MatchInParser.TryParse(text, out var mode);

        Assert.Equal(valid, ok);
        if (valid)
        {
            Assert.Equal(expected, mode.ToString());
        }
    }
}
