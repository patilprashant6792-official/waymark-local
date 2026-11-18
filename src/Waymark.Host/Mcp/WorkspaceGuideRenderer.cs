using System.Text;
using Waymark.Core.Guides;
using Waymark.Core.Workspaces;

namespace Waymark.Host.Mcp;

/// <summary>
/// Builds the guide of one workspace: its summary, the owner's instructions for it and its live setup (flows, its own gates).
/// The hash covers the owner's text only (see <see cref="WorkspaceGuideRules.Stable"/>), so saving a flow never makes a client look outdated.
/// </summary>
public sealed class WorkspaceGuideRenderer(IWorkspaceGuides texts, IWorkspaceSetup setup, ILogger<WorkspaceGuideRenderer> logger)
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
        return new ToolGuide(WorkspaceGuideRules.ModuleKey(workspace.Id), body.ToString(), WorkspaceGuideRules.Hash(workspace.Name, text));
    }

    /// <summary>The hash of the owner's text of the workspace, and whether it has instructions. Cheap: no store is read after the first call.</summary>
    public async Task<(string Hash, bool HasInstructions)> HashAsync(Workspace workspace, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var text = await texts.GetAsync(workspace.Id, ct);
        return (WorkspaceGuideRules.Hash(workspace.Name, text), text.HasInstructions);
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
