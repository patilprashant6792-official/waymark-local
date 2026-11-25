using System.Text;

namespace Waymark.Core.Memory;

/// <summary>The text embedded for one note, and the hash that tells whether the stored vector still matches it.</summary>
public sealed record EmbedText(string Text, string Hash);

/// <summary>
/// Composes what the index embeds for a note: a few header lines that place it (workspace, flow, step or about, kind), its title and then its body.
/// Pure, so a flow rename changes the text and therefore the hash, and the note is embedded again.
/// </summary>
/// </summary>
public static class NoteEmbedText
{
    public static EmbedText Compose(string workspaceName, Note note)
    {
        ArgumentNullException.ThrowIfNull(workspaceName);
        ArgumentNullException.ThrowIfNull(note);
        var text = new StringBuilder();
        text.Append("workspace: ").AppendLine(workspaceName);
        var flow = note.FlowName ?? note.DetachedFrom;
        if (flow is not null)
        {
            text.Append("flow: ").AppendLine(flow.Replace('-', ' '));
        }

        if (note.StepSpec is not null)
        {
            text.Append("step: ").AppendLine(Label(note.StepSpec));
        }
        else if (note.About is not null)
        {
            text.Append("about: ").AppendLine(note.About);
        }

        text.Append("kind: ").AppendLine(NoteKinds.ToText(note.Kind));
        text.Append("title: ").AppendLine(note.Title);
        text.Append(note.Body);
        var composed = text.ToString();
        return new EmbedText(composed, NoteRules.Hash(composed));
    }

    /// <summary>'src/A/B.cs:Type.Member' reads as 'Type.Member'; a file or a range reads as its path.</summary>
    public static string Label(string spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return FeatureComponentSpec.TryParse(spec, out var component, out _)
            ? component.Member is null
                ? component.Path
                : component.Type is null ? component.Member : $"{component.Type}.{component.Member}"
            : spec;
    }
}
