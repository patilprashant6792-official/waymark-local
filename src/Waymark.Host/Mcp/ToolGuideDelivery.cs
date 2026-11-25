using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using Waymark.Core.Guides;
using Waymark.Core.Tools;
using Waymark.Core.Workspaces;

namespace Waymark.Host.Mcp;

/// <summary>What guides added to one tool result, for the audit row.</summary>
public readonly record struct GuideAdded(int Bytes, int Tokens);

/// <summary>
/// Puts a guide into a tool result when the agent asked for it with <c>guide: true</c>, and otherwise appends a one-line notice when the agent never
/// loaded a guide or holds an older one. The overview of Waymark comes first: an agent that asks for a module guide without holding the current
/// overview gets both, and a notice about the overview goes before a notice about a workspace, which goes before a notice about a module.
/// <c>waymark_guide</c> with a workspace asks for that workspace's guide (module <c>ws:{id}</c>). It never fails a tool call: a problem here is
/// logged and the result goes out without a guide.
/// </summary>
public sealed class ToolGuideDelivery(
    IGuideCatalog guides,
    GuideDeliveries deliveries,
    IWorkspaceService workspaces,
    IWorkspaceGuides workspaceGuides,
    WorkspaceGuideRenderer workspaceRenderer,
    Waymark.Core.Memory.IPinnedNotes pinnedNotes,
    IContextMeter meter,
    ILogger<ToolGuideDelivery> logger)
{
    private const string Overview = HostPromptRules.StoreKey;

    public bool IsGuided(string module) => guides.Has(module);

    /// <summary>Removes the <c>guide</c> argument, so the tool never sees it, and says whether it was true. A string 'true' counts, because models send it.</summary>
    public static bool TakeFlag(IDictionary<string, JsonElement>? arguments)
    {
        if (arguments is null || !arguments.TryGetValue(GuideRules.ArgumentName, out var value))
        {
            return false;
        }

        try
        {
            arguments.Remove(GuideRules.ArgumentName);
        }
        catch (NotSupportedException)
        {
            // A read-only argument set cannot be changed; the tool then ignores the unknown argument.
        }

        return value.ValueKind == JsonValueKind.True
            || (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var parsed) && parsed);
    }

    /// <summary>
    /// Why <c>waymark_guide</c> cannot send the guide of this workspace, or null when it can: the name must be a workspace that exists and is on.
    /// </summary>
    public async Task<string?> WorkspaceRefusalAsync(string workspaceName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceName);
        var all = await workspaces.ListAsync(ct);
        var found = Find(all, workspaceName);
        if (found is { Enabled: true })
        {
            return null;
        }

        var on = all.Where(w => w.Enabled).Select(w => w.Name).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var list = on.Count == 0 ? "none" : string.Join(", ", on);
        return found is null
            ? $"Unknown workspace '{workspaceName}'. Workspaces on: {list}. Call {GuideRules.OverviewTool} without a workspace for the overview."
            : $"The workspace '{found.Name}' is switched off in Waymark, so it has no guide to send. Ask the owner to switch it on. Workspaces on: {list}.";
    }

    /// <summary>
    /// The guide goes first in the result; a notice goes last. Returns the size that was added. <paramref name="workspaceName"/> is the workspace the
    /// call named (null for none or for '*'): with <c>waymark_guide</c> it asks for that workspace's guide, with any other tool it may add a notice about it.
    /// </summary>
    public async Task<GuideAdded> AttachAsync(CallToolResult result, string module, bool requested, string client, string? workspaceName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(result);
        try
        {
            var added = new List<string>();
            if (requested && GuideRules.IsOverview(module) && workspaceName is not null)
            {
                // waymark_guide with a workspace: that workspace's guide, with the overview in front of it when the client's copy is not current.
                var workspace = await FindAsync(workspaceName, ct);
                if (workspace is not null)
                {
                    await DeliverWorkspaceAsync(result, workspace, client, added, ct);
                    if (!await HoldsCurrentAsync(client, Overview, ct))
                    {
                        await DeliverAsync(result, Overview, client, added, ct);
                    }
                }
            }
            else if (requested)
            {
                await DeliverAsync(result, module, client, added, ct);
                if (added.Count > 0 && !GuideRules.IsOverview(module) && !await HoldsCurrentAsync(client, Overview, ct))
                {
                    // Inserted at the front, so the overview reads before the module guide.
                    await DeliverAsync(result, Overview, client, added, ct);
                }

                // A group guide is asked for at the start of each conversation; that is the moment to point at the workspace's instructions too.
                if (added.Count > 0 && !GuideRules.IsOverview(module) && workspaceName is not null)
                {
                    var reminder = await WorkspaceReminderAsync(client, workspaceName, ct);
                    if (reminder is not null)
                    {
                        result.Content.Add(new TextContentBlock { Text = reminder });
                        added.Add(reminder);
                    }
                }
            }
            else
            {
                // One notice per call, and the overview first: a client that has not seen it cannot make good use of a workspace or module notice.
                var notice = await NoticeAsync(client, Overview, ct)
                    ?? (workspaceName is null ? null : await WorkspaceNoticeAsync(client, workspaceName, ct))
                    ?? (GuideRules.IsOverview(module) ? null : await NoticeAsync(client, module, ct));
                if (notice is not null)
                {
                    result.Content.Add(new TextContentBlock { Text = notice });
                    added.Add(notice);
                }
            }

            return added.Count == 0
                ? default
                : new GuideAdded(added.Sum(text => Encoding.UTF8.GetByteCount(text)), added.Sum(text => meter.CountTokens(text)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "The {Module} guide could not be added to a tool result", module);
            return default;
        }
    }

    private async Task DeliverAsync(CallToolResult result, string module, string client, List<string> added, CancellationToken ct)
    {
        var guide = await guides.GetAsync(module, ct);
        if (guide is null)
        {
            return;
        }

        var text = GuideRules.Deliver(guide.Module, guide.Content);
        result.Content.Insert(0, new TextContentBlock { Text = text });
        added.Add(text);
        await RecordAsync(client, guide, ct);
    }

    private async Task DeliverWorkspaceAsync(CallToolResult result, Workspace workspace, string client, List<string> added, CancellationToken ct)
    {
        var guide = await workspaceRenderer.RenderAsync(workspace, ct);
        var text = GuideRules.DeliverWorkspace(workspace.Name, guide.Content);
        result.Content.Insert(0, new TextContentBlock { Text = text });
        added.Add(text);
        await RecordAsync(client, guide, ct);
    }

    private async Task<bool> HoldsCurrentAsync(string client, string module, CancellationToken ct)
    {
        var hash = await guides.GetHashAsync(module, ct);
        return hash is not null && await deliveries.HoldsAsync(client, module, hash, ct);
    }

    private async Task<string?> NoticeAsync(string client, string module, CancellationToken ct)
    {
        var hash = await guides.GetHashAsync(module, ct);
        if (hash is null)
        {
            return null;
        }

        var notice = await deliveries.CheckAsync(client, module, hash, ct);
        return notice == GuideNotice.None ? null : GuideRules.Notice(module, notice == GuideNotice.NeverLoaded);
    }

    // Only a workspace with instructions is worth a notice: without them its guide holds flows, which memory_recall gives as well.
    private async Task<string?> WorkspaceNoticeAsync(string client, string workspaceName, CancellationToken ct)
    {
        var workspace = await FindWithInstructionsAsync(workspaceName, ct);
        if (workspace is null)
        {
            return null;
        }

        var (hash, _) = await workspaceRenderer.HashAsync(workspace, ct);
        var notice = await deliveries.CheckAsync(client, WorkspaceGuideRules.ModuleKey(workspace.Id), hash, ct);
        return notice == GuideNotice.None ? null : GuideRules.WorkspaceNotice(workspace.Name, notice == GuideNotice.NeverLoaded);
    }

    // Waymark cannot tell conversations apart, so a client that holds the current workspace guide still gets the reminder: a new conversation needs it.
    private async Task<string?> WorkspaceReminderAsync(string client, string workspaceName, CancellationToken ct)
    {
        var workspace = await FindWithInstructionsAsync(workspaceName, ct);
        if (workspace is null)
        {
            return null;
        }

        var (hash, _) = await workspaceRenderer.HashAsync(workspace, ct);
        var notice = await deliveries.CheckAsync(client, WorkspaceGuideRules.ModuleKey(workspace.Id), hash, ct);
        return notice == GuideNotice.None
            ? GuideRules.WorkspaceReminder(workspace.Name)
            : GuideRules.WorkspaceNotice(workspace.Name, notice == GuideNotice.NeverLoaded);
    }

    // The workspace list is read only when some workspace has instructions or pinned notes (both owner text), so calls in a Waymark without any stay as cheap as before.
    private async Task<Workspace?> FindWithInstructionsAsync(string workspaceName, CancellationToken ct)
    {
        var texts = await workspaceGuides.ListAsync(ct);
        var pinned = await pinnedNotes.WorkspacesAsync(ct);
        if (!texts.Values.Any(text => text.HasInstructions) && pinned.Count == 0)
        {
            return null;
        }

        var workspace = await FindAsync(workspaceName, ct);
        return workspace is not null && ((texts.TryGetValue(workspace.Id, out var own) && own.HasInstructions) || pinned.Contains(workspace.Id)) ? workspace : null;
    }

    private async Task<Workspace?> FindAsync(string workspaceName, CancellationToken ct) =>
        Find(await workspaces.ListAsync(ct), workspaceName);

    // Workspace names are unique ignoring case (the store says so), and the tools accept them in any case.
    private static Workspace? Find(IReadOnlyList<Workspace> all, string workspaceName) =>
        all.FirstOrDefault(w => string.Equals(w.Name, workspaceName.Trim(), StringComparison.OrdinalIgnoreCase));

    // The agent has the text now; if remembering it fails, the next call only repeats a notice.
    private async Task RecordAsync(string client, ToolGuide guide, CancellationToken ct)
    {
        try
        {
            await deliveries.RecordAsync(client, guide.Module, guide.Hash, ct);
            logger.LogInformation("Sent the {Module} guide to {Client}", guide.Module, client);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not record that {Client} received the {Module} guide", client, guide.Module);
        }
    }
}
