using Waymark.Modules.Code.Search;

namespace Waymark.Tests.Code;

public sealed class SearchRankerTests
{
    private static readonly IReadOnlyDictionary<string, int> NoCallers = new Dictionary<string, int>();

    private static SearchQuery Query(string text) => SearchQuery.Parse(text, null, 200, 16);

    private static SearchCandidate Candidate(
        string name,
        string text,
        double nameScore = 0,
        int? nameRank = null,
        int? contentRank = null,
        string label = "method",
        string path = "src/A.cs",
        bool isType = false) =>
        new($"ws|{path}|1|{name}|{label}", name, label, "ws", path, 1, name, text, isType, nameScore, nameRank, contentRank);

    private static List<string> Names(IReadOnlyList<RankedHit> hits) => hits.Select(h => h.Candidate.Name).ToList();

    [Fact]
    public void AnExactNameComesFirstWhateverItsRank()
    {
        var hits = SearchRanker.Rank(
            [Candidate("UserServiceFactory", "UserServiceFactory", 500, 1), Candidate("UserService", "UserService", 1000, 5)],
            Query("UserService"),
            60,
            NoCallers,
            legacyOrder: false);

        Assert.Equal(["UserService", "UserServiceFactory"], Names(hits));
    }

    [Fact]
    public void ForANameLookupAPrefixMatchBeatsAContentOnlyMatch()
    {
        var hits = SearchRanker.Rank(
            [Candidate("Other", "Other mentions userserv", 0, null, 1), Candidate("UserServiceFactory", "UserServiceFactory", 500, 3)],
            Query("userserv"),
            60,
            NoCallers,
            legacyOrder: false);

        Assert.Equal("UserServiceFactory", Names(hits)[0]);
    }

    [Fact]
    public void AHitThatCoversMoreQueryWordsWins()
    {
        var hits = SearchRanker.Rank(
            [Candidate("OnlyToken", "OnlyToken token", contentRank: 1), Candidate("Everything", "Everything refresh token rotate", contentRank: 2)],
            Query("refresh token rotate"),
            60,
            NoCallers,
            legacyOrder: false);

        Assert.Equal(["Everything", "OnlyToken"], Names(hits));
    }

    [Fact]
    public void ListsTheQueryWordsAHitMatched()
    {
        var hits = SearchRanker.Rank(
            [Candidate("Rotate", "Rotate refresh", contentRank: 1)],
            Query("refresh token rotate"),
            60,
            NoCallers,
            legacyOrder: false);

        Assert.Equal(["refresh", "rotate"], hits[0].Matched);
    }

    [Fact]
    public void TermsCountHalfAWord()
    {
        var query = SearchQuery.Parse("refresh token", ["jwt"], 200, 16);
        var text = QueryMatching.Prepare("nothing but jwt");

        Assert.Equal(0.25, SearchRanker.Coverage(query, text), 3);
    }

    [Fact]
    public void CoverageIsOneForANameLookup()
    {
        Assert.Equal(1, SearchRanker.Coverage(Query("UserService"), QueryMatching.Prepare("unrelated")));
    }

    [Fact]
    public void AWordOnlyInTheContextCountsAboutAThird()
    {
        var query = Query("alpha beta");

        Assert.Equal(0.65, SearchRanker.Coverage(query, QueryMatching.Prepare("alpha"), QueryMatching.Prepare("src/beta.cs")), 3);
    }

    [Fact]
    public void TestCodeRanksBelowProductionCode()
    {
        var hits = SearchRanker.Rank(
            [Candidate("AaaInTests", "refresh token", contentRank: 1, path: "tests/A.cs"), Candidate("InSource", "refresh token", contentRank: 1, path: "src/A.cs")],
            Query("refresh token"),
            60,
            NoCallers,
            legacyOrder: false);

        Assert.Equal(["InSource", "AaaInTests"], Names(hits));
    }

    [Fact]
    public void AMethodThatIsCalledOftenRanksHigher()
    {
        var callers = new Dictionary<string, int> { ["Popular"] = 500 };

        var hits = SearchRanker.Rank(
            [Candidate("Alpha", "refresh token", contentRank: 1), Candidate("Popular", "refresh token", contentRank: 1)],
            Query("refresh token"),
            60,
            callers,
            legacyOrder: false);

        Assert.Equal("Popular", Names(hits)[0]);
    }

    [Fact]
    public void AConstructorIsFoldedIntoItsType()
    {
        var hits = SearchRanker.Rank(
            [Candidate("Store", "Store", 1000, 1, label: "class", isType: true), Candidate("Store", "Store", 1000, 2, label: "constructor")],
            Query("Store"),
            60,
            NoCallers,
            legacyOrder: false);

        Assert.Single(hits);
        Assert.Equal("class", hits[0].Candidate.Label);
    }

    [Fact]
    public void AConstructorWithoutItsTypeStays()
    {
        var hits = SearchRanker.Rank([Candidate("Store", "Store", 1000, 1, label: "constructor")], Query("Store"), 60, NoCallers, legacyOrder: false);

        Assert.Single(hits);
    }

    [Fact]
    public void AnExcludedWordRemovesAHit()
    {
        var hits = SearchRanker.Rank(
            [Candidate("Good", "refresh token", contentRank: 1), Candidate("Old", "refresh token legacy", contentRank: 2)],
            Query("refresh token -legacy"),
            60,
            NoCallers,
            legacyOrder: false);

        Assert.Equal(["Good"], Names(hits));
    }

    [Fact]
    public void ARequiredWordMustBePresent()
    {
        var hits = SearchRanker.Rank(
            [Candidate("WithIt", "refresh token rotate", contentRank: 2), Candidate("Without", "refresh token", contentRank: 1)],
            Query("refresh token +rotate"),
            60,
            NoCallers,
            legacyOrder: false);

        Assert.Equal(["WithIt"], Names(hits));
    }

    [Fact]
    public void APhraseMustBeAdjacent()
    {
        var hits = SearchRanker.Rank(
            [Candidate("Adjacent", "RefreshTokenStore", contentRank: 2), Candidate("Apart", "token cache then refresh", contentRank: 1)],
            Query("\"refresh token\""),
            60,
            NoCallers,
            legacyOrder: false);

        Assert.Equal(["Adjacent"], Names(hits));
    }

    [Fact]
    public void APhraseDoesNotRunFromBodyWordsIntoTheName()
    {
        var hits = SearchRanker.Rank(
            [Candidate("RefreshAsync", "RefreshAsync\u0002Task RefreshAsync(CancellationToken ct)", contentRank: 1) with { Terms = "token" }],
            Query("\"token refresh\""),
            60,
            NoCallers,
            legacyOrder: false);

        Assert.Empty(hits);
    }

    [Fact]
    public void AWordOnlyInThePathCountsLessThanOneInTheSymbol()
    {
        var hits = SearchRanker.Rank(
            [Candidate("InPath", "InPath alpha", contentRank: 1) with { Context = "src/Beta.cs" }, Candidate("InSymbol", "InSymbol alpha beta", contentRank: 1)],
            Query("alpha beta"),
            60,
            NoCallers,
            legacyOrder: false);

        Assert.Equal(["InSymbol", "InPath"], Names(hits));
    }

    [Fact]
    public void ALookupByOneWordIgnoresAContentHitThatMatchesOnlyThePath()
    {
        var hits = SearchRanker.Rank(
            [Candidate("Unrelated", "Unrelated", contentRank: 1) with { Context = "Owner\u0002src/Widget.cs" }, Candidate("WidgetMaker", "WidgetMaker", contentRank: 2)],
            Query("widget"),
            60,
            NoCallers,
            legacyOrder: false);

        Assert.Equal(["WidgetMaker"], Names(hits));
    }

    [Fact]
    public void ANameEqualToTheQueryWordsIsPinnedFirst()
    {
        var hits = SearchRanker.Rank(
            [Candidate("BetterContent", "refresh token refresh token", contentRank: 1), Candidate("RefreshToken", "RefreshToken", nameScore: 150, nameRank: 1)],
            Query("refresh token"),
            60,
            NoCallers,
            legacyOrder: false);

        Assert.Equal("RefreshToken", Names(hits)[0]);
    }

    [Fact]
    public void LegacyOrderSortsByNameScoreThenNameAndSkipsTheWeights()
    {
        var hits = SearchRanker.Rank(
            [Candidate("Zed", "Zed", 500, 2, path: "tests/Z.cs"), Candidate("Beta", "Beta", 500, 3), Candidate("Exact", "Exact", 1000, 1, label: "constructor")],
            Query("x"),
            60,
            NoCallers,
            legacyOrder: true);

        Assert.Equal(["Exact", "Beta", "Zed"], Names(hits));
    }

    [Fact]
    public void ReciprocalRankFusionFavoursTheBetterRank()
    {
        Assert.True(SearchRanker.RrfScore(1, 60) > SearchRanker.RrfScore(2, 60));
        Assert.Equal(0, SearchRanker.RrfScore(null, 60));
    }

    [Theory]
    [InlineData("class", 1.15)]
    [InlineData("constructor", 0.5)]
    [InlineData("field", 0.85)]
    [InlineData("method", 1.0)]
    public void WeighsKinds(string label, double expected)
    {
        Assert.Equal(expected, SearchRanker.KindWeight(label), 3);
    }
}
