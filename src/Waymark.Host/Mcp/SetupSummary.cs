using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Waymark.Core.Commands;
using Waymark.Core.Guides;
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
    TimeProvider time) : ISetupSummary
{
    private const string None = "none";

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
        text.Append("- Workspaces on: ").AppendLine(Names(folders.Where(w => w.Enabled).Select(w => w.Name)));
        text.Append("- Workspaces off: ").AppendLine(Names(folders.Where(w => !w.Enabled).Select(w => w.Name)));
        text.Append("- Tools switched off (not in your list; ask the owner to turn one on): ").AppendLine(Names(off));

        // Auto is what most operations do, so only the ones that ask or are refused are listed.
        var byGate = CommandCatalog.Operations
            .GroupBy(op => CommandCatalog.Resolve(op, null, gates))
            .ToDictionary(g => g.Key, g => g.Select(op => op.Key));
        text.AppendLine("- Command operations that ask the owner first (a workspace can set its own gates; every other operation runs at once):");
        text.Append("  - Ask: ").AppendLine(Names(byGate.GetValueOrDefault(CommandGate.Ask, [])));
        text.Append("  - Off (refused): ").Append(Names(byGate.GetValueOrDefault(CommandGate.Off, [])));
        return text.ToString();
    }

    private static string Names(IEnumerable<string> names)
    {
        var list = names.ToList();
        return list.Count == 0 ? None : string.Join(", ", list);
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
