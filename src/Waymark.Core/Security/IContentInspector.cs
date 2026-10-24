namespace Waymark.Core.Security;

/// <summary>
/// Answers "does this text hold anything that must not leave unseen?" for text that a tool moves on the server without Claude reading it (file copies).
/// It uses the same checks as the result guard: the vault values and the recognisable credential shapes. Findings carry kinds and counts, never a value.
/// </summary>
public interface IContentInspector
{
    /// <summary>The kinds found in the text; empty when the text is clean. A finished redaction marker counts as a finding.</summary>
    Task<IReadOnlyList<RedactionFinding>> InspectAsync(string text, CancellationToken ct);
}
