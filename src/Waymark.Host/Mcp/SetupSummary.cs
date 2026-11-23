using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Waymark.Core.Commands;
using Waymark.Core.Guides;
using Waymark.Core.Memory;
using Waymark.Core.Settings;
using Waymark.Core.Tools;
using Waymark.Core.Workspaces;

namespace Waymark.Host.Mcp;

/// <summary>
/// Tells an agent what is different from the defaults right now, so it does not try a tool the owner turned off or a command that asks first.
/// It holds names and switches only: no paths, no secrets, nothing the agent could not learn from its own tool list or the owner.
/// </summary>
public sealed class SetupSummary(
    IToolRegistry registry,
    IToolSettingsStore toolSettings,
    IConfigStore config,
    IWorkspaceService workspaces,
    ICommandPolicyStore policy,
    IFeatureMapStore featureMaps,
    IWorkspaceGuides workspaceGuides,
    TimeProvider time) : ISetupSummary
{
    private const string None = "none";
    private const int MaxWorkspaceSummaryChars = WorkspaceGuideRules.MaxSummaryChars;

    public async Task<string> DescribeAsync(CancellationToken ct)
    {
        var rows = await toolSettings.ListAsync(ct);
        var gates = await policy.ListGatesAsync(ct);
        var folders = await workspaces.ListAsync(ct);
        var paused = await IsPausedAsync(ct);

        var off = registry.Tools
            .Where(tool =>
            {
                var row = rows.FirstOrDefault(r => string.Equals(r.ToolName, tool.Name, StringComparison.OrdinalIgnoreCase));
                return !(row?.Enabled ?? ToolDefaults.IsOnByDefault(tool));
            })
            .Select(tool => tool.Name)
            .Order(StringComparer.Ordinal);

        var text = new StringBuilder();
        text.Append("Snapshot taken ").Append(time.GetUtcNow().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture))
            .AppendLine(". Call waymark_guide for a fresh one.");
        text.AppendLine(paused
            ? "- State: PAUSED. Every tool call is refused until the owner resumes Waymark in the admin UI."
            : "- State: running.");
        text.Append("- Computer: ").AppendLine(RuntimeInformation.OSDescription);
        // One line per workspace. Its flows, its own gates and the owner's instructions for it are in its own guide, so this list stays short
        // however many workspaces and flows there are.
        var texts = await workspaceGuides.ListAsync(ct);
        var counts = await featureMaps.CountByWorkspaceAsync(ct);
        text.Append("- Workspaces (each has its own guide with the owner's instructions for it, its flows and its own command gates: call ")
            .Append(GuideRules.OverviewTool)
            .AppendLine(" with workspace='<name>' before you work in one; 'has instructions' marks those the owner wrote instructions for):");
        if (folders.Count == 0)
        {
            text.AppendLine("  - none");
        }

        foreach (var folder in folders.OrderBy(w => w.Name, StringComparer.OrdinalIgnoreCase))
        {
            text.AppendLine(WorkspaceLine(folder, texts.GetValueOrDefault(folder.Id), counts.GetValueOrDefault(folder.Id)));
        }
        text.Append("- Tools switched off (not in your list; ask the owner to turn one on): ").AppendLine(Names(off));

        // Auto is what most operations do, so only the ones that ask or are refused are listed.
        var byGate = CommandCatalog.Operations
            .GroupBy(op => CommandCatalog.Resolve(op, null, gates))
            .ToDictionary(g => g.Key, g => g.Select(op => op.Key));
        text.AppendLine("- Command operations that ask the owner first (a workspace can set its own gates, listed in its guide; every other operation runs at once):");
        text.Append("  - Ask: ").AppendLine(Names(byGate.GetValueOrDefault(CommandGate.Ask, [])));
        text.Append("  - Off (refused): ").Append(Names(byGate.GetValueOrDefault(CommandGate.Off, [])));
        return text.ToString();
    }

    private static string Names(IEnumerable<string> names)
    {
        var list = names.ToList();
        return list.Count == 0 ? None : string.Join(", ", list);
    }

    // One line on one workspace: its name, whether it is on, the owner's summary, how many flows it has and whether the owner wrote instructions for it.
    private static string WorkspaceLine(Workspace workspace, WorkspaceGuideText? text, int flows)
    {
        var line = new StringBuilder();
        line.Append("  - ").Append(workspace.Name).Append(workspace.Enabled ? " (on)" : " (off)");
        var summary = text?.Summary ?? string.Empty;
        if (summary.Length > MaxWorkspaceSummaryChars)
        {
            summary = summary[..(MaxWorkspaceSummaryChars - 3)] + "...";
        }

        if (summary.Length > 0)
        {
            line.Append(": ").Append(summary);
        }

        line.Append(string.Create(CultureInfo.InvariantCulture, $" · {flows} {(flows == 1 ? "flow" : "flows")}"));
        if (text?.HasInstructions == true)
        {
            line.Append(" · has instructions");
        }

        return line.ToString();
    }

    // A value that cannot be read counts as paused, the same way the call pipeline treats it.
    private async Task<bool> IsPausedAsync(CancellationToken ct)
    {
        var setting = await config.GetAsync(SettingScope.Global, SettingKeys.Paused, ct);
        if (setting is null)
        {
            return false;
        }

        try
        {
            return JsonSerializer.Deserialize<bool>(setting.ValueJson);
        }
        catch (JsonException)
        {
            return true;
        }
    }
}
