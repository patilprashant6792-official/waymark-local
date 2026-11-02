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
        "Returns feature maps saved by earlier sessions: the files, types, members and line ranges that were needed for a feature.\n" +
        "CALL IT FIRST when you start work on a feature. action='list' (optional query, page, pageSize) finds maps; action='get' (ids='12,15', optional maxTokens, default 6000) returns the current outline or source of every component in ONE call.\n" +
        "A component that is not in the code on this branch is listed as not available, never hidden or guessed.\n" +
        "A map is a hint: keep exploring where it falls short, then update it with memorise before you finish.")]
    public Task<string> Recall(
        [Description("Workspace name as shown in Waymark (a single workspace; '*' is not accepted)")]
        string workspace,
        [Description("'list' or 'get'")]
        string action,
        [Description("list: words that must all appear in a map's name, summary, components or notes")]
        string? query = null,
        [Description("list: page number, starting at 1 (default 1)")]
        int? page = null,
        [Description("list: maps per page (default 20, at most 50)")]
        int? pageSize = null,
        [Description("get: ids from list, comma separated (at most 5)")]
        string? ids = null,
        [Description("get: token budget for the returned code (default 6000)")]
        int? maxTokens = null,
        CancellationToken cancellationToken = default) =>
        RunAsync(() => ActionOf(action) switch
        {
            "list" => memory.ListAsync(workspace, query, page, pageSize, cancellationToken),
            "get" => memory.GetAsync(workspace, ids, maxTokens, cancellationToken),
            _ => throw new ArgumentException($"Unknown action '{action}'. recall accepts: list, get.")
        });

    [McpServerTool(Name = "memorise", ReadOnly = false, Destructive = false, Idempotent = true)]
    [Description(
        "Saves what you learned about a feature so the next session can recall it in one call. Call it without being asked: as soon as you have found the components of a feature, after you change one (add the new components and remove the ones you deleted or renamed), and before your final answer on any task that explored or changed a feature.\n" +
        "action='save_feature_map': name (kebab-case, e.g. 'command-gates'), components (one per line) and optional summary, remove. Saving to an existing name merges, so update the map of the feature instead of creating a near-duplicate; only what you list in remove is dropped.\n" +
        "Component forms: 'path' (file outline), 'path:Type' (type outline), 'path:Type.Member' or 'path:function' (source), 'path#L10-40' (lines); add ' | why it matters' for a note. Each must exist now.\n" +
        "action='delete_feature_map' with name removes a map.")]
    public Task<string> Memorise(
        [Description("Workspace name as shown in Waymark (a single workspace; '*' is not accepted)")]
        string workspace,
        [Description("'save_feature_map' or 'delete_feature_map'")]
        string action,
        [Description("The feature's name: lowercase letters, digits and '-' (at most 60 characters)")]
        string? name = null,
        [Description("save: one component per line, e.g. 'src/Orders/OrderService.cs:OrderService.Place | total is computed here'. At most 40 lines; a map holds at most 30 components")]
        string? components = null,
        [Description("save: one or two sentences on what the feature does (at most 300 characters). Left out, the old summary stays")]
        string? summary = null,
        [Description("save: components to drop from the map, one per line, written as they were saved")]
        string? remove = null,
        CancellationToken cancellationToken = default) =>
        RunAsync(() => ActionOf(action) switch
        {
            "save_feature_map" => memory.SaveAsync(workspace, name, summary, components, remove, cancellationToken),
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
            logger.LogWarning(ex, "A feature map tool could not read a file.");
            throw new McpException("A file could not be read. See the Waymark log for details.");
        }
    }
}
