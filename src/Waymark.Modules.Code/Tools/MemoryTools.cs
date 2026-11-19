using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Waymark.Core.Security;
using Waymark.Modules.Code.Memory;

namespace Waymark.Modules.Code.Tools;

[McpServerToolType]
public sealed class MemoryTools(IFeatureMemoryService memory, ILogger<MemoryTools> logger)
{
    [McpServerTool(Name = "recall", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description(
        "Returns saved flows, your memory of this codebase from earlier sessions: each flow is one feature as ordered method steps, from its trigger (an endpoint, UI event, job) to its outcome. Reads only.\n" +
        "CALL IT FIRST when you start work on a feature, before searching the code.\n" +
        "action='list': a table of flows (id, name, steps, version, summary, last update), paged. Without query it lists all; query keeps the flows whose name, summary, steps or notes contain all its words, " +
        "and a method name finds every flow that method is a step of.\n" +
        "action='get' with ids from list (up to 5): each step's signature and file location, whether its link to the step above still holds in the code, and which steps are not on this branch (kept, not deleted). " +
        "It never returns code: read only the steps your task needs with code_method.\n" +
        "A flow is a hint, not proof: check it in the code and keep exploring where it falls short. When your change alters the path, update the flow with memorise, passing the v shown here as expectedVersion.")]
    public Task<string> Recall(
        [Description("Workspace name as shown in Waymark (a single workspace; '*' is not accepted)")]
        string workspace,
        [Description("'list' or 'get'")]
        string action,
        [Description("list: words that must all appear in a flow's name, summary, steps or notes; a method name finds the flows it is in")]
        string? query = null,
        [Description("list: page number, starting at 1 (default 1)")]
        int? page = null,
        [Description("list: flows per page (default 20, at most 50)")]
        int? pageSize = null,
        [Description("get: ids from list, comma separated (at most 5)")]
        string? ids = null,
        CancellationToken cancellationToken = default) =>
        RunAsync(() => ActionOf(action) switch
        {
            "list" => memory.ListAsync(workspace, query, page, pageSize, cancellationToken),
            "get" => memory.GetAsync(workspace, ids, cancellationToken),
            _ => throw new ArgumentException($"Unknown action '{action}'. recall accepts: list, get.")
        });

    [McpServerTool(Name = "memorise", ReadOnly = false, Destructive = true, Idempotent = false)]
    [Description(
        "Saves or deletes a flow: one trigger to one outcome as ordered method steps, kept for later sessions and returned by recall.\n" +
        "STEPS, one per line: 'path:Type.Member', indented 2 spaces under the step that calls it; the first line is the only trigger. After a step add ~event, ~http, ~ui, ~di or ~job for a link a direct call cannot show, " +
        "and ' | note' for why it matters. Each new step must exist in the code now and each new link must be a call the code makes; otherwise nothing is saved and the reply lists the problems. Text that looks like a secret is refused.\n" +
        "action='save_feature_map': create with name, summary and steps. To change a flow, read it with recall and send its whole new steps with expectedVersion (the v shown); newName renames it. " +
        "If the flow changed since you read it, nothing is saved and the reply shows the current flow to merge with. A new flow that starts at the same step as another is refused unless distinct says how it differs.\n" +
        "action='delete_feature_map' with name: removes a flow for good, e.g. one of two flows that describe the same trigger and outcome.\n" +
        "WHEN TO SAVE: after a change that adds, removes, renames or moves steps, or for a new trigger. An edit inside a step needs no save. Steps saved before stay, whatever branch they are on.\n" +
        "RETURNS: the flow's id and new version with a count of its steps and links, the deletion, or the refusal with what to fix.")]
    public Task<string> Memorise(
        [Description("Workspace name as shown in Waymark (a single workspace; '*' is not accepted)")]
        string workspace,
        [Description("'save_feature_map' or 'delete_feature_map'")]
        string action,
        [Description("The flow's name: lowercase letters, digits and '-' (at most 60 characters), after the trigger, e.g. 'memorise-save'")]
        string? name = null,
        [Description("save: the steps, one per line, e.g. 'src/Orders/OrderService.cs:OrderService.Place | totals are computed here', indented under the step that calls them. At most 30 steps, 8 levels")]
        string? steps = null,
        [Description("save: one or two sentences on what the flow does, from trigger to outcome (at most 300 characters). Required to create; left out when changing, the old one stays")]
        string? summary = null,
        [Description("save: the version (v) of the flow you read with recall. Leave out to create a new flow")]
        int? expectedVersion = null,
        [Description("save, with expectedVersion: a new name for the flow")]
        string? newName = null,
        [Description("save, to create: how this flow differs from another that starts at the same step (at most 120 characters)")]
        string? distinct = null,
        CancellationToken cancellationToken = default) =>
        RunAsync(() => ActionOf(action) switch
        {
            "save_feature_map" => memory.SaveAsync(workspace, name, summary, steps, expectedVersion, newName, distinct, cancellationToken),
            "delete_feature_map" => memory.DeleteAsync(workspace, name, cancellationToken),
            _ => throw new ArgumentException($"Unknown action '{action}'. memorise accepts: save_feature_map, delete_feature_map.")
        });

    private static string ActionOf(string? action) => (action ?? string.Empty).Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');

    private async Task<string> RunAsync(Func<Task<string>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FileAccessDeniedException or ArgumentException or KeyNotFoundException or InvalidOperationException)
        {
            throw new McpException(ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "A flow tool could not read a file.");
            throw new McpException("A file could not be read. See the Waymark log for details.");
        }
    }
}
