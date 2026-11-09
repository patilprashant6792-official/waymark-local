using Waymark.Modules.Files;

namespace Waymark.Tests.Files;

public sealed class LinePatcherTests
{
    private static List<string> Lines(params string[] lines) => [.. lines];

    private static PatchOperation Patch(int start, int end, string? content) => new(PatchAction.Patch, start, end, content);

    private static PatchOperation Insert(int after, string? content) => new(PatchAction.Insert, after, 0, content);

    private static PatchOperation Delete(int start, int end) => new(PatchAction.Delete, start, end);

    private static PatchOperation Append(string? content) => new(PatchAction.Append, Content: content);

    private static string Fail(List<string> lines, params PatchOperation[] patches)
    {
        var before = lines.ToList();
        var exception = Assert.Throws<PatchValidationException>(() => LinePatcher.Apply(lines, patches));
        Assert.Equal(before, lines);
        return exception.Message;
    }

    [Fact]
    public void PatchReplacesTheRangeWithTheNewLines()
    {
        var lines = Lines("a", "b", "c", "d");

        var affected = LinePatcher.Apply(lines, [Patch(2, 3, "X\nY\nZ")]);

        Assert.Equal(["a", "X", "Y", "Z", "d"], lines);
        Assert.Equal(2, affected);
    }

    [Fact]
    public void PatchWithEmptyContentLeavesABlankLine()
    {
        var lines = Lines("a", "b");

        LinePatcher.Apply(lines, [Patch(2, 2, string.Empty)]);

        Assert.Equal(["a", string.Empty], lines);
    }

    [Fact]
    public void DeleteRemovesTheRange()
    {
        var lines = Lines("a", "b", "c", "d");

        var affected = LinePatcher.Apply(lines, [Delete(2, 3)]);

        Assert.Equal(["a", "d"], lines);
        Assert.Equal(2, affected);
    }

    [Theory]
    [InlineData(0, new[] { "X", "a", "b" })]
    [InlineData(1, new[] { "a", "X", "b" })]
    [InlineData(2, new[] { "a", "b", "X" })]
    public void InsertPutsTheContentAfterTheGivenLine(int after, string[] expected)
    {
        var lines = Lines("a", "b");

        var affected = LinePatcher.Apply(lines, [Insert(after, "X")]);

        Assert.Equal(expected, lines);
        Assert.Equal(1, affected);
    }

    [Fact]
    public void AppendAddsAtTheEndEvenToAnEmptyFile()
    {
        var lines = Lines();

        var affected = LinePatcher.Apply(lines, [Append("X\nY")]);

        Assert.Equal(["X", "Y"], lines);
        Assert.Equal(2, affected);
    }

    [Fact]
    public void AppendIsAppliedAfterTheRangedPatchesWhateverTheOrder()
    {
        var lines = Lines("a", "b");

        LinePatcher.Apply(lines, [Append("END"), Patch(1, 1, "A")]);

        Assert.Equal(["A", "b", "END"], lines);
    }

    [Fact]
    public void LineNumbersRefertoTheFileAsItWasBeforeAnyEditOfTheCall()
    {
        var lines = Lines("a", "b", "c", "d");

        var affected = LinePatcher.Apply(lines, [Delete(4, 4), Patch(1, 1, "A"), Insert(2, "X")]);

        Assert.Equal(["A", "b", "X", "c"], lines);
        Assert.Equal(3, affected);
    }

    [Fact]
    public void NoPatchesChangeNothing()
    {
        var lines = Lines("a");

        Assert.Equal(0, LinePatcher.Apply(lines, []));
        Assert.Equal(["a"], lines);
    }

    [Fact]
    public void AdjacentRangesDoNotOverlap()
    {
        var lines = Lines("a", "b", "c");

        LinePatcher.Apply(lines, [Patch(1, 2, "X"), Delete(3, 3)]);

        Assert.Equal(["X"], lines);
    }

    [Fact]
    public void OverlappingRangesAreRefusedAndNothingChanges() =>
        Assert.Equal(
            "Patches overlap: patch lines 1-2 conflicts with delete lines 2-3.",
            Fail(Lines("a", "b", "c", "d"), Patch(1, 2, "X"), Delete(2, 3)));

    [Fact]
    public void AnInsertConflictsWithAPatchThatCoversTheNextLine() =>
        Assert.Equal(
            "Patches overlap: insert after line 2 conflicts with delete lines 3-3.",
            Fail(Lines("a", "b", "c", "d"), Insert(2, "N"), Delete(3, 3)));

    [Fact]
    public void TwoInsertsAfterTheSameLineConflict() =>
        Assert.Contains("Patches overlap", Fail(Lines("a", "b"), Insert(1, "X"), Insert(1, "Y")));

    [Fact]
    public void ABadPatchStopsTheWholeSetEvenWhenAnEarlierOneIsFine() =>
        Assert.Contains("out of range", Fail(Lines("a", "b"), Patch(1, 1, "X"), Delete(5, 5)));

    [Fact]
    public void EveryPatchNeedsAnAction() =>
        Assert.Equal("Every patch needs an action: patch, insert, delete or append.", Fail(Lines("a"), new PatchOperation(null, 1, 1, "x")));

    [Theory]
    [InlineData(0, 1, "Patch: startLine 0 is out of range (the file has 4 lines).")]
    [InlineData(5, 5, "Patch: startLine 5 is out of range (the file has 4 lines).")]
    [InlineData(3, 2, "Patch: endLine 2 must be at least startLine 3.")]
    [InlineData(2, 5, "Patch: endLine 5 is out of range (the file has 4 lines).")]
    public void PatchRangesAreChecked(int start, int end, string expected) =>
        Assert.Equal(expected, Fail(Lines("a", "b", "c", "d"), Patch(start, end, "x")));

    [Theory]
    [InlineData(0, 1, "Delete: startLine 0 is out of range (the file has 4 lines).")]
    [InlineData(2, 9, "Delete: endLine 9 is out of range (the file has 4 lines).")]
    public void DeleteRangesAreChecked(int start, int end, string expected) =>
        Assert.Equal(expected, Fail(Lines("a", "b", "c", "d"), Delete(start, end)));

    [Theory]
    [InlineData(-1)]
    [InlineData(5)]
    public void InsertPositionIsChecked(int after) =>
        Assert.Equal($"Insert: startLine {after} is out of range (0 to 4; 0 prepends).", Fail(Lines("a", "b", "c", "d"), Insert(after, "x")));

    [Fact]
    public void PatchingAnEmptyFileIsOutOfRange() =>
        Assert.Equal("Patch: startLine 1 is out of range (the file has 0 lines).", Fail(Lines(), Patch(1, 1, "x")));

    [Fact]
    public void PatchInsertAndAppendNeedContent()
    {
        Assert.Equal("Patch needs content.", Fail(Lines("a"), Patch(1, 1, null)));
        Assert.Equal("Insert needs content.", Fail(Lines("a"), Insert(0, null)));
        Assert.Equal("Append needs content.", Fail(Lines("a"), Append(null)));
    }

    [Fact]
    public void DeleteNeedsNoContent()
    {
        var lines = Lines("a", "b");

        LinePatcher.Apply(lines, [new PatchOperation(PatchAction.Delete, 1, 1, null)]);

        Assert.Equal(["b"], lines);
    }

    [Fact]
    public void AnUnknownActionIsRefused() =>
        Assert.Equal("Unknown action 99.", Fail(Lines("a"), new PatchOperation((PatchAction)99, 1, 1, "x")));
}
