using Waymark.Core.Memory;

namespace Waymark.Tests.Memory;

public sealed class NoteRulesTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    private static readonly NoteAuthor Owner = new(NoteAuthorKind.Human, "admin-ui");
    private static readonly NoteAuthor OtherHuman = new(NoteAuthorKind.Human, "someone-else");
    private static readonly NoteAuthor Claude = new(NoteAuthorKind.ClaudeClient, "claude-ai");
    private static readonly NoteAuthor OtherClaude = new(NoteAuthorKind.ClaudeClient, "claude-code");
    private static readonly NoteAuthor Internal = new(NoteAuthorKind.InternalModel, "claude-ai");

    [Theory]
    [InlineData("  one   idea \n\n  per note  ", "one idea per note")]
    [InlineData("tab\tseparated", "tab separated")]
    [InlineData("x", "x")]
    public void CleanTrimsAndMakesOneLine(string body, string expected) =>
        Assert.Equal(expected, NoteRules.Clean(body));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n ")]
    public void CleanRefusesAnEmptyNote(string? body) =>
        Assert.Throws<ArgumentException>(() => NoteRules.Clean(body));

    [Fact]
    public void CleanRefusesANoteOverTheCap()
    {
        Assert.Equal(NoteRules.MaxBodyChars, NoteRules.Clean(new string('a', NoteRules.MaxBodyChars)).Length);
        Assert.Throws<ArgumentException>(() => NoteRules.Clean(new string('a', NoteRules.MaxBodyChars + 1)));
    }

    [Fact]
    public void CleanRefusesControlCharacters() =>
        Assert.Throws<ArgumentException>(() => NoteRules.Clean("bell\u0007here"));

    [Fact]
    public void TheAuthorRevisesOnlyTheirOwnNote()
    {
        Assert.True(NoteRules.CanRevise(Claude, NoteBy(Claude)));
        Assert.True(NoteRules.CanRevise(Owner, NoteBy(Owner)));
        Assert.False(NoteRules.CanRevise(Claude, NoteBy(Owner)));
        Assert.False(NoteRules.CanRevise(Claude, NoteBy(OtherClaude)));
        Assert.False(NoteRules.CanRevise(Owner, NoteBy(Claude)));
        Assert.False(NoteRules.CanRevise(OtherHuman, NoteBy(Owner)));
        Assert.False(NoteRules.CanRevise(Internal, NoteBy(Claude)));
    }

    [Fact]
    public void TheOwnerRetiresAnyNoteAModelOnlyItsOwn()
    {
        Assert.True(NoteRules.CanRetire(Owner, NoteBy(Claude)));
        Assert.True(NoteRules.CanRetire(Owner, NoteBy(OtherHuman)));
        Assert.True(NoteRules.CanRetire(Claude, NoteBy(Claude)));
        Assert.False(NoteRules.CanRetire(Claude, NoteBy(Owner)));
        Assert.False(NoteRules.CanRetire(Claude, NoteBy(OtherClaude)));
        Assert.False(NoteRules.CanRetire(Internal, NoteBy(Claude)));
    }

    [Fact]
    public void ConfirmFollowsTheRetireRule()
    {
        Assert.True(NoteRules.CanConfirm(Owner, NoteBy(Claude)));
        Assert.False(NoteRules.CanConfirm(Claude, NoteBy(Owner)));
    }

    [Fact]
    public void OnlyAHumanPins()
    {
        Assert.True(NoteRules.CanPin(Owner));
        Assert.False(NoteRules.CanPin(Claude));
        Assert.False(NoteRules.CanPin(Internal));
    }

    [Fact]
    public void ThePinBudgetCountsNotesAndCharacters()
    {
        var eleven = Enumerable.Range(1, 11).Select(i => NoteBy(Owner, i, "short")).ToList();
        Assert.True(NoteRules.PinFits(eleven, NoteBy(Owner, 99, "one more")));

        var twelve = Enumerable.Range(1, 12).Select(i => NoteBy(Owner, i, "short")).ToList();
        Assert.False(NoteRules.PinFits(twelve, NoteBy(Owner, 99, "one more")));
        Assert.True(NoteRules.PinFits(twelve, twelve[0]));

        var long1 = NoteBy(Owner, 1, new string('a', 1500));
        Assert.False(NoteRules.PinFits([long1], NoteBy(Owner, 2, new string('b', 600))));
    }

    [Theory]
    [InlineData("gotcha", NoteKind.Gotcha)]
    [InlineData("How-To", NoteKind.Howto)]
    [InlineData("", NoteKind.Note)]
    public void KindsAreReadInAnyCase(string text, NoteKind expected)
    {
        Assert.True(NoteKinds.TryParse(text, out var kind));
        Assert.Equal(expected, kind);
    }

    [Fact]
    public void AnUnknownKindIsRefused() => Assert.False(NoteKinds.TryParse("rule", out _));

    [Fact]
    public void OneLineCutsAndFlattens()
    {
        Assert.Equal("a b", NoteRules.OneLine("a\nb"));
        Assert.Equal(10, NoteRules.OneLine(new string('x', 50), 10).Length);
    }

    private static Note NoteBy(NoteAuthor author, long id = 1, string body = "body") =>
        new(id, 1, null, null, null, null, null, NoteKind.Note, body, NoteRules.Hash(body), author, null, null, null, null, null, null, null, false, At, At);
}
