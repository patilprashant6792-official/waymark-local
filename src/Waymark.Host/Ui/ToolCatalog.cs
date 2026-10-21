using Waymark.Core.Audit;
using Waymark.Core.Tools;

namespace Waymark.Host.Ui;

/// <param name="HasSavedSwitch">The owner has set this tool explicitly; otherwise it follows ToolDefaults (Read on, Confirm off).</param>
/// <param name="LastUsedUtc">From the most recent calls in the audit trail; null when it is not among them.</param>
public sealed record ToolRow(ToolDescriptor Tool, bool Enabled, bool HasSavedSwitch, DateTimeOffset? LastUsedUtc)
{
    public int Tokens => ToolCatalog.EstimateTokens(Tool.DescriptionChars);
}

/// <summary>Joins the tool registry, the saved switches and the audit trail into the rows the Home and Tools pages show.</summary>
public sealed class ToolCatalog(IToolRegistry registry, IToolSettingsStore settings, IAuditStore audit)
{
    private const int RecentCalls = 500;
    private const int CharsPerToken = 4;

    /// <summary>A rough guess: text length divided by four. The page says so.</summary>
    public static int EstimateTokens(int chars) => Math.Max(1, (chars + CharsPerToken - 1) / CharsPerToken);

    public async Task<IReadOnlyList<ToolRow>> ListAsync(CancellationToken ct)
    {
        var saved = (await settings.ListAsync(ct)).ToDictionary(s => s.ToolName, StringComparer.OrdinalIgnoreCase);
        var recent = await audit.QueryAsync(new AuditQuery(AuditCategory.Tool, null, null, RecentCalls), ct);
        var lastUsed = recent
            .Where(e => e.Entry.Outcome != AuditOutcome.Denied)
            .GroupBy(e => e.Entry.Action, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Max(e => e.Entry.TimestampUtc), StringComparer.OrdinalIgnoreCase);

        return registry.Tools
            .Select(tool =>
            {
                var hasSaved = saved.TryGetValue(tool.Name, out var row);
                var enabled = hasSaved ? row!.Enabled : ToolDefaults.IsOnByDefault(tool);
                return new ToolRow(tool, enabled, hasSaved, lastUsed.TryGetValue(tool.Name, out var at) ? at : null);
            })
            .ToList();
    }
}
