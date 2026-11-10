using Waymark.Modules.Code.Search;

namespace Waymark.Tests.Code;

public sealed class SymbolScorerTests
{
    private static double Score(string name, params string[] tokens) => SymbolScorer.Score(name, tokens, caseSensitive: false);

    [Fact]
    public void ScoresTheTiersInOrder()
    {
        Assert.Equal(1000, Score("UserService", "userservice"));
        Assert.Equal(500, Score("UserService", "user"));
        Assert.Equal(300, Score("ProjectSkeletonService", "PSS"));
        Assert.Equal(200, Score("ProjectSkeletonService", "skeleton"));
        Assert.Equal(100, Score("ProjectSkeletonService", "tonServ"));
    }

    [Fact]
    public void SeveralWordsMustAllBeInTheName()
    {
        Assert.Equal(150, Score("SearchGloballyAsync", "search", "global"));
        Assert.Equal(0, Score("SearchGloballyAsync", "search", "refresh"));
    }

    [Fact]
    public void CharactersInOrderScoreBelowEverySubstringMatch()
    {
        var fuzzy = Score("ReadAllText", "ret");

        Assert.InRange(fuzzy, 1, SymbolScorer.FuzzyCeiling - 1);
    }

    [Fact]
    public void TwoLettersNeverMatchByCharactersInOrder()
    {
        Assert.Equal(0, Score("ReadAllText", "rt"));
    }

    [Fact]
    public void TwoLettersStillMatchAsAPrefixOrInitials()
    {
        Assert.Equal(500, Score("ReadAllText", "re"));
        Assert.True(Score("ReadAllText", "ra") >= 200);
    }

    [Fact]
    public void CaseSensitiveMatchingHonoursCase()
    {
        Assert.Equal(0, SymbolScorer.Score("UserService", ["userservice"], caseSensitive: true));
        Assert.Equal(1000, SymbolScorer.Score("UserService", ["UserService"], caseSensitive: true));
    }
}
