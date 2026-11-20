using Waymark.Core.Memory;
using Waymark.Core.Workspaces;

namespace Waymark.Modules.Code.Memory;

/// <summary>
/// Turns a note's anchor into the fingerprint of the code it points at now, through the same reader recall uses (path guard, parsers).
/// An anchor that is not on this branch, or that cannot be read, has no fingerprint: null, never an error.
/// </summary>
internal sealed class NoteAnchors(FlowStepReader reader)
{
    /// <summary>The canonical text of an 'about' anchor: 'path' or 'path:Type.Member'.</summary>
    /// <exception cref="ArgumentException">Not a file or a member of one.</exception>
    public static string CleanAbout(string about)
    {
        if (!FeatureComponentSpec.TryParse(about, out var component, out var error))
        {
            throw new ArgumentException($"about must be a path from the workspace root ('src/App/Orders.cs') or a member of one ('src/App/Orders.cs:OrderService.Place'). {error}");
        }

        if (component.IsRange)
        {
            throw new ArgumentException("about names a file or a member, not a line range: line numbers move, a name stays.");
        }

        return FeatureComponentSpec.Format(component);
    }

    /// <param name="files">The files read so far in this call, so a file shared by several notes is read once.</param>
    public async Task<string?> HashAsync(Workspace workspace, string? spec, Dictionary<string, FileView> files, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(files);
        if (spec is null || !FeatureComponentSpec.TryParse(spec, out var component, out _))
        {
            return null;
        }

        var step = new FlowStep(component, 0, FlowLinkKind.Call, string.Empty, FeatureComponentSpec.Format(component));
        var view = await reader.DescribeAsync(workspace, step, files, ct).ConfigureAwait(false);
        return StepFingerprint.Of(view);
    }

    /// <summary>The code anchor of a note: its step for a step note, its 'about' for a workspace note; null for a flow note or a plain workspace note.</summary>
    public static string? CodeSpecOf(Note note)
    {
        ArgumentNullException.ThrowIfNull(note);
        return note.StepSpec ?? note.About;
    }
}
