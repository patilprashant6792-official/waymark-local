using Waymark.Modules.Code.Search;

namespace Waymark.Tests.Code;

public sealed class QueryMatchingTests
{
    [Theory]
    [InlineData("creating")]
    [InlineData("created")]
    [InlineData("creates")]
    [InlineData("create")]
    public void InflectionsShareAStem(string word)
    {
        Assert.Equal("creat", QueryMatching.Stem(word));
    }

    [Theory]
    [InlineData("string")]
    [InlineData("class")]
    [InlineData("use")]
    public void ShortWordsAreLeftAlone(string word)
    {
        Assert.Equal(word, QueryMatching.Stem(word));
    }

    [Fact]
    public void FindsAWordInsideAnIdentifier()
    {
        Assert.True(QueryMatching.Matches("creating", QueryMatching.Prepare("CreateUser")));
        Assert.True(QueryMatching.Matches("token", QueryMatching.Prepare("refresh_token")));
    }

    [Fact]
    public void FindsTheJoinedWordAcrossSeparators()
    {
        Assert.True(QueryMatching.Matches("refreshtoken", QueryMatching.Prepare("refresh_token")));
    }

    [Fact]
    public void DoesNotFindAWordThatIsNotThere()
    {
        Assert.False(QueryMatching.Matches("rotate", QueryMatching.Prepare("CreateUser")));
    }

    [Fact]
    public void ASymbolOnlyWordMatchesNothing()
    {
        Assert.False(QueryMatching.Matches("!!", QueryMatching.Prepare("CreateUser")));
    }

    [Fact]
    public void OrderedWordsSplitIdentifiers()
    {
        Assert.Equal(["refresh", "the", "http", "client"], QueryMatching.OrderedWords("Refresh the HttpClient"));
    }

    [Fact]
    public void APhraseNeedsAdjacentWordsInOrder()
    {
        var words = QueryMatching.OrderedWords("RefreshTokenStore and token refresh");

        Assert.True(QueryMatching.ContainsPhrase(words, ["refresh", "token"]));
        Assert.True(QueryMatching.ContainsPhrase(words, ["token", "refresh"]));
        Assert.False(QueryMatching.ContainsPhrase(words, ["store", "refresh"]));
    }

    [Fact]
    public void APhraseWordMatchesTheStartOfATextWord()
    {
        var words = QueryMatching.OrderedWords("refreshes tokens");

        Assert.True(QueryMatching.ContainsPhrase(words, ["refresh", "token"]));
    }
}
