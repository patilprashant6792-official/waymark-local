using System.Globalization;
using System.Text;
using Waymark.Core.Guides;
using Waymark.Core.Memory;
using Waymark.Core.Workspaces;

namespace Waymark.Host.Mcp;

/// <summary>
/// Builds the guide of one workspace: its summary, the owner's instructions for it and its live setup (flows, pinned notes, its own gates).
/// The hash covers the owner's text only, instructions and pinned notes (see <see cref="WorkspaceGuideRules.Stable"/>), and whether search by meaning is on,
/// so saving a flow never makes a client look outdated but a new pin or switching search by meaning does.
/// </summary>
public sealed class WorkspaceGuideRenderer(IWorkspaceGuides texts, IWorkspaceSetup setup, IPinnedNotes pinned, ILogger<WorkspaceGuideRenderer> logger)
{
    public async Task<ToolGuide> RenderAsync(Workspace workspace, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var text = await texts.GetAsync(workspace.Id, ct);
        var body = new StringBuilder();
        body.Append("# Workspace ").Append(workspace.Name).Append("\n\n");
        if (text.Summary.Length > 0)
        {
            body.Append(text.Summary).Append("\n\n");
        }

        if (text.HasInstructions)
        {
            body.Append(WorkspaceGuideRules.InstructionsBlock(workspace.Name, text.Instructions));
        }
        else
        {
            body.Append("The owner wrote no instructions for this workspace: the owner's instructions in the overview and Waymark's defaults apply.\n\n");
        }

        body.Append("## Live setup\n\n").Append(await DescribeSetupAsync(workspace, ct));
        var pins = await PinsAsync(workspace, ct);
        var meaning = await setup.MeaningOnAsync(workspace.Id, ct);
        return new ToolGuide(WorkspaceGuideRules.ModuleKey(workspace.Id), body.ToString(), WorkspaceGuideRules.Hash(workspace.Name, text, pins, meaning));
    }

    /// <summary>
    /// The hash of the owner's text of the workspace (instructions and pinned notes) with whether search by meaning is on, and whether there is any owner text.
    /// Cheap: all of it is cached after the first call.
    /// </summary>
    public async Task<(string Hash, bool HasInstructions)> HashAsync(Workspace workspace, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var text = await texts.GetAsync(workspace.Id, ct);
        var pins = await PinsAsync(workspace, ct);
        var meaning = await setup.MeaningOnAsync(workspace.Id, ct);
        return (WorkspaceGuideRules.Hash(workspace.Name, text, pins, meaning), text.HasInstructions || pins.Count > 0);
    }

    // 'id:content hash' per pinned note. A failure leaves the pins out of the hash rather than failing the call.
    private async Task<IReadOnlyList<string>> PinsAsync(Workspace workspace, CancellationToken ct)
    {
        try
        {
            var notes = await pinned.GetAsync(workspace.Id, ct);
            return [.. notes.Select(note => string.Create(CultureInfo.InvariantCulture, $"{note.Id}:{note.ContentHash}"))];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "The pinned notes of workspace {Workspace} could not be read for its guide hash", workspace.Name);
            return [];
        }
    }

    // A failure here costs the agent the flows only; the owner's instructions are still worth sending.
    private async Task<string> DescribeSetupAsync(Workspace workspace, CancellationToken ct)
    {
        try
        {
            return await setup.DescribeAsync(workspace, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "The live setup of workspace {Workspace} could not be described", workspace.Name);
            return $"The live setup could not be read just now. Call {GuideRules.OverviewTool} with workspace='{workspace.Name}' again later for it, or use recall with action 'list'.";
        }
    }
}
