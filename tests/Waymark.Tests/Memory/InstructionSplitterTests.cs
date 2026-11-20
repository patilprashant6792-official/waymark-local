using Waymark.Core.Memory;

namespace Waymark.Tests.Memory;

public sealed class InstructionSplitterTests
{
    [Fact]
    public void EachListItemIsOneNoteWithItsHeading()
    {
        var notes = InstructionSplitter.Split("# Testing\n\n- Never call a real API in tests.\n- Use TestDatabase for SQLite.\n");

        Assert.Equal(2, notes.Count);
        Assert.Equal("Testing: Never call a real API in tests.", notes[0].Body);
        Assert.Equal(NoteKind.Gotcha, notes[0].Kind);
        Assert.Equal("Testing: Use TestDatabase for SQLite.", notes[1].Body);
        Assert.Equal(NoteKind.Convention, notes[1].Kind);
    }

    [Fact]
    public void AParagraphIsOneNoteOnOneLine()
    {
        var notes = InstructionSplitter.Split("We decided to keep one writer\nbecause SQLite locks.\n\nOther text.");

        Assert.Equal(2, notes.Count);
        Assert.Equal("We decided to keep one writer because SQLite locks.", notes[0].Body);
        Assert.Equal(NoteKind.Decision, notes[0].Kind);
    }

    [Fact]
    public void AnIndentedItemContinuesTheOneAbove()
    {
        var notes = InstructionSplitter.Split("- Build with cmd_project\n  - then run the tests\n- Other");

        Assert.Equal(2, notes.Count);
        Assert.Equal("Build with cmd_project then run the tests", notes[0].Body);
    }

    [Fact]
    public void ACodeBlockStaysWithItsText()
    {
        var notes = InstructionSplitter.Split("To run:\n```\ndotnet test\n```");

        var note = Assert.Single(notes);
        Assert.Contains("dotnet test", note.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void ALongPieceIsCutAtSentenceEnds()
    {
        var sentence = new string('a', 700) + ".";
        var notes = InstructionSplitter.Split(sentence + " " + sentence);

        Assert.Equal(2, notes.Count);
        Assert.All(notes, note => Assert.True(note.Body.Length <= NoteRules.MaxBodyChars));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("# Only a heading")]
    public void NothingGivesNoNotes(string? text) => Assert.Empty(InstructionSplitter.Split(text));
}
