using System.Globalization;
using System.Text;
using Waymark.Core.Commands;
using Waymark.Core.Guides;
using Waymark.Core.Memory;
using Waymark.Core.Workspaces;

namespace Waymark.Host.Mcp;

/// <summary>
/// Tells an agent what is live in one workspace: the flows saved for it and the command gates the owner set for it alone.
/// Names and switches only, like the overview's live setup: no paths, no secrets.
/// </summary>
public sealed class WorkspaceSetup(IFeatureMapStore featureMaps, ICommandPolicyStore policy, TimeProvider time) : IWorkspaceSetup
{
    internal const int MaxMapNamesPerWorkspace = 15;
    private const int MaxSummaryChars = 60;

    public async Task<string> DescribeAsync(Workspace workspace, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var text = new StringBuilder();
        text.Append("Snapshot taken ").Append(time.GetUtcNow().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture))
            .Append(". Call ").Append(GuideRules.OverviewTool).Append(" with workspace='").Append(workspace.Name).AppendLine("' for a fresh one.");
        text.AppendLine(workspace.Enabled
            ? "- State: on."
            : "- State: OFF. Every tool call in this workspace is refused until the owner switches it on in the admin UI.");

        var index = await featureMaps.ListNamesAsync(MaxMapNamesPerWorkspace, ct);
        text.AppendLine("- Flows saved (a flow is one trigger to one outcome as ordered method steps; recall with action 'get' and an id describes one in a single call, with no code; action 'list' searches by words or by a method name; memorise saves or changes one):");
        if (!index.TryGetValue(workspace.Id, out var entry) || entry.Total == 0)
        {
            text.AppendLine("  - none yet");
        }
        else
        {
            foreach (var map in entry.Newest)
            {
                text.Append("  - ").AppendLine(FlowLine(map));
            }

            if (entry.Total > entry.Newest.Count)
            {
                text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  - and {entry.Total - entry.Newest.Count} more (action 'list')"));
            }
        }

        // Only what differs from the overview's list: the gates the owner set for this workspace alone.
        var gates = await policy.ListGatesAsync(ct);
        var own = CommandCatalog.Operations
            .Select(op => (Key: op.Key, Here: CommandCatalog.Resolve(op, workspace.Id, gates), Everywhere: CommandCatalog.Resolve(op, null, gates)))
            .Where(row => row.Here != row.Everywhere)
            .OrderBy(row => row.Key, StringComparer.Ordinal)
            .ToList();
        text.Append("- Command gates set for this workspace only (they replace the overview's list here): ");
        text.Append(own.Count == 0
            ? "none"
            : string.Join(", ", own.Select(row => $"{row.Key} {GateWord(row.Here)}")));
        return text.ToString();
    }

    // One line on one flow: enough for an agent to pick it without listing first. The summary is cut short; recall get has the rest.
    internal static string FlowLine(FeatureMapName map)
    {
        ArgumentNullException.ThrowIfNull(map);
        var steps = $"{map.StepCount} {(map.StepCount == 1 ? "step" : "steps")}{(map.Ordered ? string.Empty : ", unordered")}";
        var summary = map.Summary.Replace('\n', ' ').Replace('\r', ' ').Trim();
        if (summary.Length > MaxSummaryChars)
        {
            summary = summary[..(MaxSummaryChars - 3)] + "...";
        }

        return summary.Length == 0 ? $"{map.Id} {map.Name} ({steps})" : $"{map.Id} {map.Name} ({steps}): {summary}";
    }

    private static string GateWord(CommandGate gate) => gate switch
    {
        CommandGate.Off => "off (refused)",
        CommandGate.Ask => "asks the owner first",
        _ => "runs at once",
    };
}
