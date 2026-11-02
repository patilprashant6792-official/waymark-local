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
        "Returns saved flows: a feature as ordered method steps from its trigger to its outcome, kept by earlier sessions.\n" +
        "CALL IT FIRST when you start work on a feature. action='list' (optional query: words, or a method name to find the flows it is a step of; page, pageSize) finds flows; action='get' (ids='12,15') returns each step's signature, location and whether its link to the step above still holds. It never returns code: read only the steps your task needs, with code_method.\n" +
        "A step that is not in the code on this branch is listed as such and kept. A flow is a hint: keep exploring where it falls short.")]
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

    [McpServerTool(Name = "memorise", ReadOnly = false, Destructive = false, Idempotent = true)]
    [Description(
        "Saves a flow: one trigger to one outcome as method steps, one per line: 'path:Type.Member', indented 2 spaces per level for 'called by the step above'. The first line is the only trigger. Add ~event, ~http, ~ui, ~di or ~job after a step for a link a call cannot show, and ' | note' for why it matters. Each new step must exist now and each new link must be a call the code makes.\n" +
        "action='save_feature_map': to create, give name, summary and steps. To change a flow you read with recall, give its whole new steps and expectedVersion (the v shown); newName renames it. Save only when the path changed (steps added, removed, renamed or moved) or for a new trigger; an edit inside a step needs no save. Steps that were saved before stay, whatever branch they are on.\n" +
        "A flow that starts at the same step as another is refused unless distinct says how it differs. If two flows describe the same trigger and outcome, delete one with action='delete_feature_map' and name.")]
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
