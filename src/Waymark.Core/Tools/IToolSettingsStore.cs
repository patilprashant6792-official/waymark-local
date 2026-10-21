namespace Waymark.Core.Tools;

/// <summary>A tool's switch as saved by the owner. A tool with no row follows its default.</summary>
public sealed record ToolSetting(string ToolName, bool Enabled, DateTimeOffset ChangedUtc, string ChangedBy);

public interface IToolSettingsStore
{
    Task<IReadOnlyList<ToolSetting>> ListAsync(CancellationToken ct);

    Task SetAsync(string toolName, bool enabled, string changedBy, DateTimeOffset now, CancellationToken ct);

    /// <summary>Sets several tools in one transaction, so a profile switch is all or nothing.</summary>
    Task SetManyAsync(IReadOnlyCollection<string> toolNames, bool enabled, string changedBy, DateTimeOffset now, CancellationToken ct);
}
