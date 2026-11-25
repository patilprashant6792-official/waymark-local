using Waymark.Core.Memory;

namespace Waymark.Host.Components.Shared;

/// <summary>What the note editor hands back: the title, the text, the kind and, for a workspace note, what it is about.</summary>
public sealed record NoteEditorValue(string Title, string Body, NoteKind Kind, string? About);
