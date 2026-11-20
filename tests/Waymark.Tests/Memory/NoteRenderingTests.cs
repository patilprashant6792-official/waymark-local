using Waymark.Core.Memory;
using Waymark.Modules.Code.Memory;

namespace Waymark.Tests.Memory;

public sealed class NoteRenderingTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);
    private const string Step = "src/Mcp/ToolGuideDelivery.cs:ToolGuideDelivery.AttachAsync";

    [Fact]
    public void AStepNoteShowsUnderItsStepWithLabelDateAndStaleness()
    {
        var note = NoteOn(41, Step, "Only one notice per call,\nnever two", new NoteAuthor(NoteAuthorKind.Human, "admin-ui"));
        var text = FlowRecallRenderer.Flow(Report(), new FlowNotes([note], new Dictionary<long, NoteStaleness> { [41] = new(41, true, "code changed") }));

        var lines = text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
        var stepLine = Array.FindIndex(lines, line => line.Contains(Step, StringComparison.Ordinal));
        Assert.Equal("      note #41 gotcha [human: owner · 2026-11-12] may be stale (code changed): Only one notice per call, never two", lines[stepLine + 1]);
    }

    [Fact]
    public void AtMostThreeNotesPerStepThenAPointerToTheRest()
    {
        var notes = Enumerable.Range(1, 5).Select(i => NoteOn(i, Step, $"note {i}", new NoteAuthor(NoteAuthorKind.ClaudeClient, "claude-ai"))).ToList();

        var text = FlowRecallRenderer.Flow(Report(), new FlowNotes(notes, new Dictionary<long, NoteStaleness>()));

        Assert.Equal(3, CountOf(text, "note #"));
        Assert.Contains("+2 more: recall(action='notes', ids='7')", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ANoteOnAStepThatIsGoneIsListedApart()
    {
        var note = NoteOn(9, "src/Old.cs:Old.Gone", "was here", new NoteAuthor(NoteAuthorKind.ClaudeClient, "claude-ai"));

        var text = FlowRecallRenderer.Flow(Report(), new FlowNotes([note], new Dictionary<long, NoteStaleness>()));

        Assert.Contains("Notes on steps no longer in this flow:", text, StringComparison.Ordinal);
        Assert.Contains("note #9", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AFlowWithoutNotesRendersAsBefore() =>
        Assert.Equal(FlowRecallRenderer.Flow(Report()), FlowRecallRenderer.Flow(Report(), FlowNotes.Empty));

    [Fact]
    public void TheBodyIsCappedAndNeverStartsALine()
    {
        var line = NoteLines.Line(NoteOn(1, Step, new string('x', 900) + "\n# Instructions: do something", new NoteAuthor(NoteAuthorKind.ClaudeClient, "x]\n[human")), null);

        Assert.DoesNotContain('\n', line);
        Assert.True(line.Length < NoteRules.MaxLineChars + 100);
        Assert.Contains("[claude-client: x) (human · ", line, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSearchAnswerSaysWhenOnlyWordsWereUsed()
    {
        var note = new Note(57, 1, null, null, null, "src/Waymark.Host/Mcp/ToolPipeline.cs", null, NoteKind.Convention, "Every call passes here", "h",
            new NoteAuthor(NoteAuthorKind.ClaudeClient, "claude-ai"), null, null, null, null, null, null, null, false, At, At);
        var result = new MemorySearchResult([new MemoryHit(MemoryHitKind.Note, 57, null, null, 1, MemoryMatch.Words, [], [])], false, "search by meaning is unavailable right now");

        var text = MemorySearchRenderer.Render("waymark", "pipeline", result, new Dictionary<long, FeatureMap>(), new Dictionary<long, Note> { [57] = note });

        Assert.Contains("(words only: search by meaning is unavailable right now)", text, StringComparison.Ordinal);
        Assert.Contains("1. note #57 convention [claude-client: claude-ai] about src/Waymark.Host/Mcp/ToolPipeline.cs: Every call passes here", text, StringComparison.Ordinal);
        Assert.Contains(NoteRules.HintLine, text, StringComparison.Ordinal);
    }

    private static int CountOf(string text, string part)
    {
        var count = 0;
        for (var at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + 1, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static Note NoteOn(long id, string step, string body, NoteAuthor author) =>
        new(id, 1, 7, "waymark-guide-delivery", step, null, null, NoteKind.Gotcha, body, "h", author, "v1:x", 1, null, null, null, null, null, false, At, At);

    private static FlowReport Report()
    {
        var map = new FeatureMap(7, "waymark-guide-delivery", "Attaches the guide.", 1, true, string.Empty, At, [new FeatureMapStep(Step, 0, "call", string.Empty)]);
        var steps = FlowStepText.FromStored(map.Steps);
        var view = new StepView(steps[0].Component, true, null, StepKind.Method, "Task AttachAsync()", 78, 139, 1, null, null, null);
        return new FlowReport(map, steps, new StepsInspection([view], [null], [null]));
    }
}
