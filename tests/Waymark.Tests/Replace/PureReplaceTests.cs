using System.Text;
using Waymark.Modules.Files;
using Waymark.Modules.Files.Replace;
using Waymark.Modules.Code.Rewriting;

namespace Waymark.Tests.Replace;

public sealed class LiteralMatcherTests
{
    [Fact]
    public void MatchesDoNotOverlap()
    {
        var matches = LiteralMatcher.Find("aaaa", "aa", caseSensitive: true, wholeWord: false);
        Assert.Equal(new[] { new TextRange(0, 2), new TextRange(2, 2) }, matches);
    }

    [Fact]
    public void CaseSensitiveByDefaultAndInsensitiveOnRequest()
    {
        Assert.Single(LiteralMatcher.Find("Order order ORDER", "order", caseSensitive: true, wholeWord: false));
        Assert.Equal(3, LiteralMatcher.Find("Order order ORDER", "order", caseSensitive: false, wholeWord: false).Count);
    }

    [Fact]
    public void WholeWordSkipsLongerIdentifiers()
    {
        var matches = LiteralMatcher.Find("Order OrderId _Order Order.Id x(Order)", "Order", caseSensitive: true, wholeWord: true);
        Assert.Equal(new[] { 0, 21, 32 }, matches.Select(m => m.Start));
    }

    [Fact]
    public void WholeWordChecksOnlyWordEnds()
    {
        var matches = LiteralMatcher.Find("a->Order b->Orders", "->Order", caseSensitive: true, wholeWord: true);
        Assert.Equal(new[] { 1 }, matches.Select(m => m.Start));
    }

    [Fact]
    public void WholeWordTreatsSupplementaryLettersAsWordCharacters()
    {
        // U+1D400 (mathematical bold A) is a letter outside the BMP: a match next to it is part of a word.
        var text = "\U0001D400Order Order";
        var matches = LiteralMatcher.Find(text, "Order", caseSensitive: true, wholeWord: true);
        Assert.Equal(new[] { text.Length - 5 }, matches.Select(m => m.Start));
    }

    [Fact]
    public void FindsMatchAtStartAndEnd()
    {
        Assert.Equal(new[] { 0, 4 }, LiteralMatcher.Find("ab::ab", "ab", caseSensitive: true, wholeWord: true).Select(m => m.Start));
    }
}

public sealed class SpanApplierTests
{
    [Fact]
    public void ReplacesOnlyTheRanges()
    {
        Assert.Equal("x Bar y Bar z", SpanApplier.Apply("x Foo y Foo z", [new TextRange(2, 3), new TextRange(8, 3)], "Bar"));
    }

    [Fact]
    public void EmptyReplacementDeletes()
    {
        Assert.Equal("ab", SpanApplier.Apply("a--b", [new TextRange(1, 2)], string.Empty));
    }

    [Fact]
    public void OverlappingRangesAreRefused()
    {
        Assert.Throws<ArgumentException>(() => SpanApplier.Apply("abcdef", [new TextRange(0, 3), new TextRange(2, 2)], "x"));
    }

    [Fact]
    public void RangeOutsideTheTextIsRefused()
    {
        Assert.Throws<ArgumentException>(() => SpanApplier.Apply("abc", [new TextRange(2, 5)], "x"));
    }
}

public sealed class LineMapTests
{
    [Fact]
    public void LinesWorkWithMixedEndings()
    {
        var map = new LineMap("one\r\ntwo\nthree\r\n");
        Assert.Equal(3, map.LineCount);
        Assert.Equal("one", map.LineText(1));
        Assert.Equal("two", map.LineText(2));
        Assert.Equal("three", map.LineText(3));
        Assert.Equal(2, map.LineOf(5));
        Assert.Equal(3, map.LineOf(9));
    }

    [Fact]
    public void LastLineWithoutBreak()
    {
        var map = new LineMap("a\nb");
        Assert.Equal(2, map.LineCount);
        Assert.Equal("b", map.LineText(2));
    }

    [Fact]
    public void EmptyTextHasNoLines() => Assert.Equal(0, new LineMap(string.Empty).LineCount);

    [Fact]
    public void ExcerptMarksEveryMatch()
    {
        Assert.Equal("x ⟦Foo⟧ ⟦Foo⟧", LineMap.Excerpt("    x Foo Foo", [new TextRange(6, 3), new TextRange(10, 3)], 200));
    }
}

public sealed class ReplaceExclusionsTests
{
    private static readonly IReadOnlyList<FilePlan> Plan =
    [
        new FilePlan("src/A.cs", "h1", [new PlannedSpan(0, 3, 1), new PlannedSpan(10, 3, 2), new PlannedSpan(14, 3, 2)]),
        new FilePlan("docs/a.md", "h2", [new PlannedSpan(0, 3, 5)]),
    ];

    [Fact]
    public void DropsLinesAndFiles()
    {
        var (kept, excluded) = ReplaceExclusions.Apply(Plan, ReplaceExclusions.Parse(["src/A.cs#L2", "docs/a.md"]));
        Assert.Equal(3, excluded);
        var file = Assert.Single(kept);
        Assert.Equal("src/A.cs", file.RelativePath);
        Assert.Single(file.Spans);
    }

    [Fact]
    public void UnknownFileIsRefused() =>
        Assert.Throws<ArgumentException>(() => ReplaceExclusions.Apply(Plan, ReplaceExclusions.Parse(["src/B.cs"])));

    [Fact]
    public void LineWithoutMatchIsRefused() =>
        Assert.Throws<ArgumentException>(() => ReplaceExclusions.Apply(Plan, ReplaceExclusions.Parse(["src/A.cs#L9"])));

    [Fact]
    public void BadLineNumberIsRefused() =>
        Assert.Throws<ArgumentException>(() => ReplaceExclusions.Parse(["src/A.cs#Lx"]));
}

public sealed class ReplacePlanStoreTests
{
    [Fact]
    public void PlanExpires()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var store = new ReplacePlanStore(ReplaceOptions.Defaults, time);
        var plan = store.Add(1, "sig", []);
        Assert.NotNull(store.Find(plan.Id));
        time.Now = time.Now.AddMinutes(ReplaceOptions.Defaults.PlanMinutes);
        Assert.Null(store.Find(plan.Id));
    }

    [Fact]
    public void PlanIsUsedOnce()
    {
        var store = new ReplacePlanStore(ReplaceOptions.Defaults, TimeProvider.System);
        var plan = store.Add(1, "sig", []);
        Assert.True(store.Remove(plan.Id));
        Assert.False(store.Remove(plan.Id));
        Assert.Null(store.Find(plan.Id));
    }

    [Fact]
    public void OldestPlanIsDroppedPastTheLimit()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var store = new ReplacePlanStore(ReplaceOptions.Defaults with { MaxOpenPlans = 2 }, time);
        var first = store.Add(1, "a", []);
        time.Now = time.Now.AddSeconds(1);
        store.Add(1, "b", []);
        time.Now = time.Now.AddSeconds(1);
        store.Add(1, "c", []);
        Assert.Equal(2, store.Count);
        Assert.Null(store.Find(first.Id));
    }
}

public sealed class EncodeExactTests
{
    [Theory]
    [InlineData("utf8")]
    [InlineData("utf8bom")]
    [InlineData("utf16le")]
    [InlineData("utf16be")]
    public void RoundTripKeepsEveryByte(string kind)
    {
        var encoding = kind switch
        {
            "utf8" => new UTF8Encoding(false),
            "utf8bom" => new UTF8Encoding(true),
            "utf16le" => new UnicodeEncoding(bigEndian: false, byteOrderMark: true),
            _ => (Encoding)new UnicodeEncoding(bigEndian: true, byteOrderMark: true),
        };
        var text = "line one\r\nline two\nline three\r\nno final break é";
        var original = encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray();

        var format = TextFileFormat.Detect(original);
        var decoded = format.DecodeStrict(original);

        Assert.Equal(text, decoded);
        Assert.Equal(original, format.EncodeExact(decoded));
    }
}

public sealed class ReplaceRequestValidatorTests
{
    private static ReplaceRequest Request(string? find = "a", string? with = "b", string? glob = "**", IReadOnlyList<string>? paths = null) =>
        new("test", find, with, glob, paths);

    [Fact]
    public void LineBreakInFindIsRefused() =>
        Assert.Throws<ArgumentException>(() => ReplaceRequestValidator.Validate(Request(find: "a\nb"), ReplaceOptions.Defaults));

    [Fact]
    public void SameFindAndWithIsRefused() =>
        Assert.Throws<ArgumentException>(() => ReplaceRequestValidator.Validate(Request(with: "a"), ReplaceOptions.Defaults));

    [Fact]
    public void GlobAndPathsTogetherAreRefused() =>
        Assert.Throws<ArgumentException>(() => ReplaceRequestValidator.Validate(Request(paths: ["a.txt"]), ReplaceOptions.Defaults));

    [Fact]
    public void NeitherGlobNorPathsIsRefused() =>
        Assert.Throws<ArgumentException>(() => ReplaceRequestValidator.Validate(Request(glob: null), ReplaceOptions.Defaults));

    [Fact]
    public void SignatureChangesWithEveryArgument()
    {
        var a = ReplaceRequestValidator.Validate(Request(), ReplaceOptions.Defaults).Signature;
        var b = ReplaceRequestValidator.Validate(Request() with { WholeWord = true }, ReplaceOptions.Defaults).Signature;
        var c = ReplaceRequestValidator.Validate(Request(glob: "*.cs"), ReplaceOptions.Defaults).Signature;
        Assert.NotEqual(a, b);
        Assert.NotEqual(a, c);
    }
}

public sealed class CSharpFindIdentifierTests
{
    [Fact]
    public void FindsIdentifiersButNotStringsOrComments()
    {
        const string code = "class Foo { string s = \"Foo\"; // Foo\n Foo Make() => new Foo(); string n = nameof(Foo); }";
        var search = new CSharpRewriter().FindIdentifier(code, "Foo");
        Assert.Equal(4, search.Spans.Count);
        Assert.All(search.Spans, span => Assert.Equal("Foo", code.Substring(span.Start, span.Length)));
        Assert.Equal(0, search.SyntaxErrors);
    }

    [Fact]
    public void KeywordIsRefused() =>
        Assert.Throws<ArgumentException>(() => new CSharpRewriter().FindIdentifier("class A {}", "class"));
}
