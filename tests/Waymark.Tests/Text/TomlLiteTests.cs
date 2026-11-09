using Waymark.Core.Text;

namespace Waymark.Tests.Text;

public sealed class TomlLiteTests
{
    private static string Toml(params string[] lines) => string.Join("\n", lines);

    private static TomlSection Section(IReadOnlyList<TomlSection> sections, string name) => sections.Single(s => s.Name == name);

    [Fact]
    public void KeysBeforeTheFirstHeaderBelongToAnUnnamedSection()
    {
        var sections = TomlLite.Parse(Toml("title = 'root'", "[project]", "name = 'demo'"));

        Assert.Equal(["", "project"], sections.Select(s => s.Name));
        Assert.Equal("'root'", Section(sections, "").Values["title"]);
        Assert.Equal("demo", TomlLite.AsString(Section(sections, "project").Values["name"]));
    }

    [Fact]
    public void EmptyInputGivesNoSections() => Assert.Empty(TomlLite.Parse(string.Empty));

    [Fact]
    public void TheUnnamedSectionIsDroppedWhenItHasNoKeys()
    {
        var sections = TomlLite.Parse(Toml("[a]", "k = 1"));

        Assert.Equal("a", Assert.Single(sections).Name);
    }

    [Fact]
    public void ATableWithoutKeysIsKept()
    {
        var section = Assert.Single(TomlLite.Parse("[empty]"));

        Assert.Equal("empty", section.Name);
        Assert.Empty(section.Values);
        Assert.False(section.IsArrayItem);
    }

    [Fact]
    public void NullTextIsRefused() => Assert.Throws<ArgumentNullException>(() => TomlLite.Parse(null!));

    [Fact]
    public void DottedAndSpacedHeadersAreNamed()
    {
        var sections = TomlLite.Parse(Toml("[tool.poetry.dependencies]", "python = '^3.11'", "  [ spaced ]  ", "k = 1"));

        Assert.Equal(["tool.poetry.dependencies", "spaced"], sections.Select(s => s.Name));
    }

    [Fact]
    public void ArrayTablesAreSeparateItemsWithTheSameName()
    {
        var sections = TomlLite.Parse(Toml("[[package]]", "name = 'a'", "version = '1'", "[[package]]", "name = 'b'"));

        Assert.Equal(2, sections.Count);
        Assert.All(sections, s => Assert.True(s.IsArrayItem));
        Assert.All(sections, s => Assert.Equal("package", s.Name));
        Assert.Equal("'a'", sections[0].Values["name"]);
        Assert.Equal("'1'", sections[0].Values["version"]);
        Assert.Equal("'b'", sections[1].Values["name"]);
        Assert.False(sections[1].Values.ContainsKey("version"));
    }

    [Fact]
    public void CommentsAreRemovedButNotInsideStrings()
    {
        var section = Assert.Single(TomlLite.Parse(Toml(
            "[p] # header comment",
            "# whole line",
            "name = 'x' # trailing",
            "url = 'http://a#b'  # c",
            "text = \"a # b\"")));

        Assert.Equal("p", section.Name);
        Assert.Equal("x", TomlLite.AsString(section.Values["name"]));
        Assert.Equal("http://a#b", TomlLite.AsString(section.Values["url"]));
        Assert.Equal("a # b", TomlLite.AsString(section.Values["text"]));
    }

    [Fact]
    public void ArraysMayContinueOverSeveralLines()
    {
        var section = Assert.Single(TomlLite.Parse(Toml(
            "[project]",
            "dependencies = [",
            "  'requests>=2',  # why",
            "  \"flask\",",
            "]",
            "other = 1")));

        Assert.Equal(["requests>=2", "flask"], TomlLite.Strings(section.Values["dependencies"]));
        Assert.Contains('\n', section.Values["dependencies"]);
        Assert.Equal("1", section.Values["other"]);
    }

    [Fact]
    public void BracketsInsideStringsDoNotEndAnArray()
    {
        var section = Assert.Single(TomlLite.Parse(Toml("[p]", "deps = [", "  'a]b',", "  'c'", "]", "next = 2")));

        Assert.Equal(["a]b", "c"], TomlLite.Strings(section.Values["deps"]));
        Assert.Equal("2", section.Values["next"]);
    }

    [Fact]
    public void MultiLineStringsKeepTheirLines()
    {
        var sections = TomlLite.Parse(Toml("[p]", "desc = \"\"\"", "line one", "line two", "\"\"\"", "after = 'x'"));
        var section = Assert.Single(sections);

        Assert.Equal("line one\nline two", TomlLite.AsString(section.Values["desc"]));
        Assert.Equal("x", TomlLite.AsString(section.Values["after"]));
    }

    [Fact]
    public void WindowsLineBreaksAreAccepted()
    {
        var section = Assert.Single(TomlLite.Parse("[a]\r\nk = 'v'\r\n"));

        Assert.Equal("'v'", section.Values["k"]);
    }

    [Fact]
    public void LinesThatAreNotKeyValuePairsAreSkipped()
    {
        var section = Assert.Single(TomlLite.Parse(Toml("[a]", "this is not toml", "= novalue", "k = 'v'")));

        Assert.Equal(["k"], section.Values.Keys);
    }

    [Fact]
    public void QuotedKeysLoseTheirQuotes()
    {
        var section = Assert.Single(TomlLite.Parse(Toml("[a]", "\"quoted key\" = 'v'", "'lit' = 'w'")));

        Assert.Equal("'v'", section.Values["quoted key"]);
        Assert.Equal("'w'", section.Values["lit"]);
    }

    [Fact]
    public void ALaterDuplicateKeyReplacesAnEarlierOne()
    {
        var section = Assert.Single(TomlLite.Parse(Toml("[a]", "k = 'first'", "k = 'second'")));

        Assert.Equal("second", TomlLite.AsString(section.Values["k"]));
    }

    [Fact]
    public void BrokenInputNeverThrows()
    {
        var unclosedString = Assert.Single(TomlLite.Parse("k = \"unclosed"));
        Assert.Null(TomlLite.AsString(unclosedString.Values["k"]));

        var unclosedArray = Assert.Single(TomlLite.Parse(Toml("k = [", "'a'")));
        Assert.Equal(["a"], TomlLite.Strings(unclosedArray.Values["k"]));
    }

    [Theory]
    [InlineData("\"hi\"", "hi")]
    [InlineData("'hi'", "hi")]
    [InlineData("  \"padded\"  ", "padded")]
    [InlineData("\"\"", "")]
    [InlineData("''", "")]
    [InlineData("'''raw'''", "raw")]
    [InlineData("\"\"\"triple\"\"\"", "triple")]
    [InlineData("\"\"\"\"\"\"", "")]
    [InlineData("\"\"\"\nline\n\"\"\"", "line")]
    [InlineData("\"a\\tb\"", "a\tb")]
    [InlineData("\"a\\nb\"", "a\nb")]
    [InlineData("\"say \\\"hi\\\"\"", "say \"hi\"")]
    [InlineData("'a\\nb'", "a\\nb")]
    public void AsStringReadsBasicLiteralAndTripleQuotedStrings(string raw, string expected) =>
        Assert.Equal(expected, TomlLite.AsString(raw));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("123")]
    [InlineData("true")]
    [InlineData("\"unterminated")]
    [InlineData("\"")]
    [InlineData("[\"a\"]")]
    public void AsStringIsNullForAnythingThatIsNotAString(string? raw) => Assert.Null(TomlLite.AsString(raw));

    [Fact]
    public void StringsReturnsOnlyTheTopLevelStringItemsInOrder() =>
        Assert.Equal(["a", "b"], TomlLite.Strings("[\"a\", 'b', 3, [\"nested\"]]"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("\"not an array\"")]
    [InlineData("[]")]
    [InlineData("[1, 2]")]
    public void StringsIsEmptyWhenThereIsNoStringItem(string? raw) => Assert.Empty(TomlLite.Strings(raw));
}
