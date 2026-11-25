using Waymark.Core.Memory;

namespace Waymark.Tests.Memory;

public sealed class NoteEmbedTextTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TheTextPlacesTheNoteBeforeItsBody()
    {
        var text = NoteEmbedText.Compose("waymark", Step("guide-delivery"));

        Assert.Equal("workspace: waymark\nflow: guide delivery\nstep: ToolGuideDelivery.AttachAsync\nkind: gotcha\nOnly one notice per call", text.Text.Replace("\r", string.Empty, StringComparison.Ordinal));
        Assert.Equal(NoteRules.Hash(text.Text), text.Hash);
    }

    [Fact]
    public void RenamingTheFlowChangesTheHash() =>
        Assert.NotEqual(NoteEmbedText.Compose("waymark", Step("old-name")).Hash, NoteEmbedText.Compose("waymark", Step("new-name")).Hash);

    [Fact]
    public void AWorkspaceNoteSaysWhatItIsAbout()
    {
        var note = new Note(2, 1, null, null, null, "src/A.cs", null, NoteKind.Convention, "Keep it small", "h", new NoteAuthor(NoteAuthorKind.Human, "admin-ui"), null, null, null, null, null, null, null, false, At, At);

        Assert.Contains("about: src/A.cs", NoteEmbedText.Compose("waymark", note).Text, StringComparison.Ordinal);
    }

    private static Note Step(string flow) =>
        new(1, 1, 7, flow, "src/Mcp/ToolGuideDelivery.cs:ToolGuideDelivery.AttachAsync", null, null, NoteKind.Gotcha, "Only one notice per call", "h",
            new NoteAuthor(NoteAuthorKind.Agent, "claude-ai"), null, 1, null, null, null, null, null, false, At, At);
}
