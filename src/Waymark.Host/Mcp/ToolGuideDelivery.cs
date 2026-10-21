using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using Waymark.Core.Guides;
using Waymark.Core.Tools;

namespace Waymark.Host.Mcp;

/// <summary>What guides added to one tool result, for the audit row.</summary>
public readonly record struct GuideAdded(int Bytes, int Tokens);

/// <summary>
/// Puts a guide into a tool result when the agent asked for it with <c>guide: true</c>, and otherwise appends a one-line notice when the agent never
/// loaded a guide or holds an older one. The overview of Waymark comes first: an agent that asks for a module guide without holding the current
/// overview gets both, and a notice about the overview goes before a notice about a module. It never fails a tool call: a problem here is logged
/// and the result goes out without a guide.
/// </summary>
public sealed class ToolGuideDelivery(IGuideCatalog guides, GuideDeliveries deliveries, IContextMeter meter, ILogger<ToolGuideDelivery> logger)
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

    /// <summary>The guide goes first in the result; a notice goes last. Returns the size that was added.</summary>
    public async Task<GuideAdded> AttachAsync(CallToolResult result, string module, bool requested, string client, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(result);
        try
        {
            var added = new List<string>();
            if (requested)
            {
                await DeliverAsync(result, module, client, added, ct);
                if (added.Count > 0 && !GuideRules.IsOverview(module) && !await HoldsCurrentAsync(client, Overview, ct))
                {
                    // Inserted at the front, so the overview reads before the module guide.
                    await DeliverAsync(result, Overview, client, added, ct);
                }
            }
            else
            {
                // One notice per call, and the overview first: a client that has not seen it cannot make good use of a module notice.
                var notice = await NoticeAsync(client, Overview, ct)
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
