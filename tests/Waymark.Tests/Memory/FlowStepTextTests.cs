using Waymark.Core.Memory;

namespace Waymark.Tests.Memory;

public sealed class FlowStepTextTests
{
    private const string Sample =
        "src/A.cs:A.Run | trigger\n" +
        "  src/B.cs:B.Do\n" +
        "    src/C.cs:C.Save ~event | async\n" +
        "  src/D.cs:D.Mail ~http";

    private static IReadOnlyList<FlowStep> Parse(string text)
    {
        Assert.True(FlowStepText.TryParse(text, out var steps, out var errors), string.Join(" / ", errors));
        return steps!;
    }

    private static IReadOnlyList<string> Errors(string? text)
    {
        Assert.False(FlowStepText.TryParse(text, out var steps, out var errors));
        Assert.Null(steps);
        Assert.NotEmpty(errors);
        return errors;
    }

    [Fact]
    public void AFlowIsReadStepByStepWithDepthLinkAndNote()
    {
        var steps = Parse(Sample);

        Assert.Equal([0, 1, 2, 1], steps.Select(s => s.Depth));
        Assert.Equal([FlowLinkKind.Call, FlowLinkKind.Call, FlowLinkKind.Event, FlowLinkKind.Http], steps.Select(s => s.Link));
        Assert.Equal(["trigger", "", "async", ""], steps.Select(s => s.Note));
        Assert.Equal(["src/A.cs:A.Run", "src/B.cs:B.Do", "src/C.cs:C.Save", "src/D.cs:D.Mail"], steps.Select(s => s.Spec));
        Assert.All(steps, s => Assert.True(s.Valid));
    }

    [Fact]
    public void FormatWritesTheSameTextBack() => Assert.Equal(Sample, FlowStepText.Format(Parse(Sample)));

    [Fact]
    public void WindowsLineBreaksBlankLinesListMarkersAndBackticksAreAccepted()
    {
        var steps = Parse("- `src/A.cs:A.Run`\r\n\r\n  * src/B.cs:B.Do\r\n");

        Assert.Equal(["src/A.cs:A.Run", "src/B.cs:B.Do"], steps.Select(s => s.Spec));
        Assert.Equal([0, 1], steps.Select(s => s.Depth));
    }

    [Fact]
    public void LinkKindsAreReadInAnyCase()
    {
        var steps = Parse("a.cs\n  b.cs ~UI\n  c.cs ~Di\n  d.cs ~JOB");

        Assert.Equal([FlowLinkKind.Call, FlowLinkKind.Ui, FlowLinkKind.Di, FlowLinkKind.Job], steps.Select(s => s.Link));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n \n")]
    public void AnEmptyFlowNeedsAStep(string? text) =>
        Assert.Equal("A flow needs at least one step.", Assert.Single(Errors(text)));

    [Fact]
    public void ATabIndentIsRefused() => Assert.Contains("not a tab", Assert.Single(Errors("a.cs\n\tb.cs")));

    [Fact]
    public void AnOddIndentIsRefusedWithItsWidth() =>
        Assert.Equal("Line 2: indent with 2 spaces per level, this line has 3.", Assert.Single(Errors("a.cs\n   b.cs")));

    [Fact]
    public void TheFirstLineMustNotBeIndented() =>
        Assert.Equal("Line 3: the first line is the trigger and has no indent.", Assert.Single(Errors("\n\n  a.cs")));

    [Fact]
    public void AFlowHasOneTrigger() =>
        Assert.Contains("a flow has one trigger", Assert.Single(Errors("a.cs\nb.cs")));

    [Fact]
    public void ASkippedLevelIsRefused() =>
        Assert.Contains("indented 2 levels", Assert.Single(Errors("a.cs\n    b.cs")));

    [Fact]
    public void TheTriggerTakesNoLink() =>
        Assert.Contains("the trigger has no incoming link", Assert.Single(Errors("a.cs ~http")));

    [Fact]
    public void AStepIsListedOnceIgnoringCase() =>
        Assert.Contains("already a step", Assert.Single(Errors("a.cs\n  b.cs\n  B.CS")));

    [Theory]
    [InlineData("a.cs\n  b.cs ~bogus")]
    [InlineData("a.cs\n  b.cs ~http ~ui")]
    [InlineData("a.cs\n  b.cs ~")]
    public void ABadLinkIsRefused(string text) => Assert.Contains("is not a link", Assert.Single(Errors(text)));

    [Fact]
    public void TwoComponentsOnOneLineAreRefused() =>
        Assert.Contains("one step per line", Assert.Single(Errors("a.cs b.cs")));

    [Fact]
    public void ABadComponentReportsTheLineNumber() =>
        Assert.StartsWith("Line 2: ", Assert.Single(Errors("a.cs\n  ../b.cs")));

    [Fact]
    public void ALongNoteOrAControlCharacterInANoteIsRefused()
    {
        Assert.Contains("a note is at most 120", Assert.Single(Errors("a.cs | " + new string('n', 121))));
        Assert.Contains("a note is at most 120", Assert.Single(Errors("a.cs | bad\u0001note")));
        Assert.NotEmpty(Parse("a.cs | " + new string('n', 120)));
    }

    [Fact]
    public void DepthLimitIsEightLevelsBelowTheTrigger()
    {
        static string Chain(int levels) =>
            string.Join("\n", Enumerable.Range(0, levels + 1).Select(i => new string(' ', i * 2) + $"f{i}.cs"));

        Assert.Equal(9, Parse(Chain(8)).Count);
        Assert.Contains("at most 8 levels deep", Assert.Single(Errors(Chain(9))));
    }

    [Fact]
    public void StepCountLimitIsThirty()
    {
        static string Fan(int children) =>
            "root.cs\n" + string.Join("\n", Enumerable.Range(0, children).Select(i => $"  c{i}.cs"));

        Assert.Equal(30, Parse(Fan(29)).Count);
        Assert.Contains("holds at most 30 steps and this one has 31", Assert.Single(Errors(Fan(30))));
    }

    [Fact]
    public void AtMostSixErrorsAreReportedFollowedByTheRemainingCount()
    {
        var text = string.Join("\n", Enumerable.Repeat(" a.cs", 10));

        var errors = Errors(text);

        Assert.Equal(7, errors.Count);
        Assert.Equal("And 4 more.", errors[^1]);
    }

    [Fact]
    public void OneBadLineDoesNotCauseAnErrorForEveryLineBelowIt()
    {
        var errors = Errors("a.cs\n  b.cs\n  b.cs\n    c.cs\n    d.cs");

        Assert.Single(errors);
    }

    [Fact]
    public void FormatLineAppendsTheLinkOnlyWhenItIsNotACallAndTheNoteOnlyWhenPresent()
    {
        var component = new FeatureComponent("a.cs", null, null, null, null);

        Assert.Equal("a.cs", FlowStepText.FormatLine(new FlowStep(component, 0, FlowLinkKind.Call, string.Empty, "a.cs")));
        Assert.Equal("    a.cs ~ui | why", FlowStepText.FormatLine(new FlowStep(component, 2, FlowLinkKind.Ui, "why", "a.cs")));
    }

    [Fact]
    public void ToStoredAndFromStoredRoundTrip()
    {
        var steps = Parse(Sample);

        var stored = FlowStepText.ToStored(steps);
        var back = FlowStepText.FromStored(stored);

        Assert.Equal(steps.Count, stored.Count);
        Assert.Equal("event", stored[2].Link);
        Assert.Equal("async", stored[2].Note);
        Assert.Equal(2, stored[2].Depth);
        Assert.Equal(FlowStepText.Format(steps), FlowStepText.Format(back));
    }

    [Fact]
    public void StoredTextThatIsNoLongerAComponentIsKeptAndMarkedInvalid()
    {
        var stored = new[]
        {
            new FeatureMapStep("src/A.cs:A.Run", 0, "call", string.Empty),
            new FeatureMapStep("not a valid spec", 1, "weird", "kept"),
        };

        var steps = FlowStepText.FromStored(stored);

        Assert.True(steps[0].Valid);
        Assert.False(steps[1].Valid);
        Assert.Equal("not a valid spec", steps[1].Spec);
        Assert.Equal("kept", steps[1].Note);
        Assert.Equal(FlowLinkKind.Call, steps[1].Link);
    }

    [Fact]
    public void ParentIndexFindsTheNearestShallowerStepAbove()
    {
        var steps = Parse(Sample);

        Assert.Null(FlowStepText.ParentIndex(steps, 0));
        Assert.Equal(0, FlowStepText.ParentIndex(steps, 1));
        Assert.Equal(1, FlowStepText.ParentIndex(steps, 2));
        Assert.Equal(0, FlowStepText.ParentIndex(steps, 3));
        Assert.Null(FlowStepText.ParentIndex(steps, -1));
        Assert.Null(FlowStepText.ParentIndex(steps, 99));
    }

    [Fact]
    public void HasChildrenIsTrueWhenTheNextStepIsDeeper()
    {
        var steps = Parse(Sample);

        Assert.True(FlowStepText.HasChildren(steps, 0));
        Assert.True(FlowStepText.HasChildren(steps, 1));
        Assert.False(FlowStepText.HasChildren(steps, 2));
        Assert.False(FlowStepText.HasChildren(steps, 3));
        Assert.False(FlowStepText.HasChildren(steps, -1));
        Assert.False(FlowStepText.HasChildren(steps, 99));
    }

    [Fact]
    public void NullStepListsAreRefused()
    {
        Assert.Throws<ArgumentNullException>(() => FlowStepText.Format(null!));
        Assert.Throws<ArgumentNullException>(() => FlowStepText.FormatLine(null!));
        Assert.Throws<ArgumentNullException>(() => FlowStepText.ToStored(null!));
        Assert.Throws<ArgumentNullException>(() => FlowStepText.FromStored(null!));
        Assert.Throws<ArgumentNullException>(() => FlowStepText.ParentIndex(null!, 0));
        Assert.Throws<ArgumentNullException>(() => FlowStepText.HasChildren(null!, 0));
    }
}

public sealed class FlowLinkKindsTests
{
    [Theory]
    [InlineData(FlowLinkKind.Call, "call")]
    [InlineData(FlowLinkKind.Http, "http")]
    [InlineData(FlowLinkKind.Event, "event")]
    [InlineData(FlowLinkKind.Ui, "ui")]
    [InlineData(FlowLinkKind.Di, "di")]
    [InlineData(FlowLinkKind.Job, "job")]
    public void EveryKindHasATextAndParsesBack(FlowLinkKind kind, string text)
    {
        Assert.Equal(text, FlowLinkKinds.ToText(kind));
        Assert.True(FlowLinkKinds.TryParse(text, out var parsed));
        Assert.Equal(kind, parsed);
        Assert.Equal(kind, FlowLinkKinds.FromStored(text));
    }

    [Theory]
    [InlineData(" HTTP ", FlowLinkKind.Http)]
    [InlineData("Event", FlowLinkKind.Event)]
    public void ParsingIgnoresCaseAndSurroundingSpaces(string text, FlowLinkKind expected)
    {
        Assert.True(FlowLinkKinds.TryParse(text, out var kind));
        Assert.Equal(expected, kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("grpc")]
    public void UnknownTextIsNotAKindAndFallsBackToCall(string? text)
    {
        Assert.False(FlowLinkKinds.TryParse(text, out var kind));
        Assert.Equal(FlowLinkKind.Call, kind);
        Assert.Equal(FlowLinkKind.Call, FlowLinkKinds.FromStored(text));
    }

    [Fact]
    public void AnUndefinedKindPrintsAsCall() => Assert.Equal("call", FlowLinkKinds.ToText((FlowLinkKind)99));
}
