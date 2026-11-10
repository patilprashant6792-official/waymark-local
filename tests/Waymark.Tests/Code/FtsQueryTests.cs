using Waymark.Modules.Code.Search;

namespace Waymark.Tests.Code;

public sealed class FtsQueryTests
{
    [Fact]
    public void OneWordIsAQuotedPrefix()
    {
        Assert.Equal("{name full_name tokens doc path terms} : (\"token\"*)", FtsQuery.Build(["token"], FtsQuery.AllColumns, all: true));
    }

    [Fact]
    public void JoinsWordsWithAndOrOr()
    {
        Assert.Equal("{doc} : (\"refresh\"* AND \"token\"*)", FtsQuery.Build(["refresh", "token"], FtsQuery.DocColumns, all: true));
        Assert.Equal("{terms} : (\"refresh\"* OR \"token\"*)", FtsQuery.Build(["refresh", "token"], FtsQuery.CodeColumns, all: false));
    }

    [Fact]
    public void ACamelCaseWordAlsoMatchesItsPartsSideBySide()
    {
        var expression = FtsQuery.Build(["RefreshToken"], FtsQuery.DocColumns, all: true);

        Assert.Equal("{doc} : ((\"refreshtoken\"* OR \"refresh token\"))", expression);
    }

    [Fact]
    public void NoQuerySyntaxGetsThrough()
    {
        var expression = FtsQuery.Build(["a\" OR x", "NEAR(", "*"], FtsQuery.AllColumns, all: true);

        Assert.NotNull(expression);
        Assert.DoesNotContain("\" OR x", expression, StringComparison.Ordinal);
        Assert.DoesNotContain("NEAR", expression, StringComparison.Ordinal);
        Assert.Equal(0, expression.Count(c => c == '"') % 2);
    }

    [Fact]
    public void WordsWithoutLettersOrDigitsGiveNoQuery()
    {
        Assert.Null(FtsQuery.Build(["!!!", "--"], FtsQuery.AllColumns, all: true));
    }

    [Fact]
    public void RepeatedWordsAppearOnce()
    {
        Assert.Equal("{doc} : (\"token\"*)", FtsQuery.Build(["token", "Token"], FtsQuery.DocColumns, all: true));
    }

    [Theory]
    [InlineData("All", "{name full_name tokens doc path terms}")]
    [InlineData("Docs", "{doc}")]
    [InlineData("Code", "{terms}")]
    public void ChoosesColumnsByMode(string mode, string expected)
    {
        Assert.Equal(expected, FtsQuery.ColumnsFor(Enum.Parse<MatchIn>(mode)));
    }
}
