using System.Text;
using Microsoft.Extensions.Logging;
using Waymark.Core.Memory;
using Waymark.Core.Security;
using Waymark.Core.Tools;

namespace Waymark.Modules.Code.Memory;

/// <summary>
/// recall: finds flows (by words, or by a method name to see which flows it is a step of) and describes them step by step with what the code says now.
/// It never returns code: the agent reads the steps it needs with code_method.
/// </summary>
internal sealed class FlowRecallService(
    IPathGuard guard,
    IFeatureMapStore store,
    FlowInspector inspector,
    IFlowSemanticIndex semantic,
    ToolPipelineOptions pipeline,
    ILogger<FlowRecallService> logger)
{
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 50;
    private const int ReservedChars = 600;
    private const int SimilarTake = 5;

    public async Task<string> ListAsync(string? workspace, string? query, int? page, int? pageSize, CancellationToken ct)
    {
        var found = await guard.GetWorkspaceAsync(workspace, ct).ConfigureAwait(false);
        var size = Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize);
        var number = Math.Max(1, page ?? 1);
        var words = string.IsNullOrWhiteSpace(query) ? null : query.Trim();
        var result = await store.ListAsync(found.Id, words, (number - 1) * size, size, ct).ConfigureAwait(false);
        var similar = await SimilarAsync(found.Id, words, result.Items, ct).ConfigureAwait(false);
        if (result.Total == 0 && similar.Count == 0)
        {
            return words is null
                ? $"No flows are saved for workspace '{found.Name}' yet. After you trace a feature from its trigger to its outcome, save it with memorise(action='save_feature_map')."
                : $"No flow matches '{words}' in workspace '{found.Name}'. recall(action='list') without a query shows all of them.";
        }

        var pages = Math.Max(1, (result.Total + size - 1) / size);
        return FlowRecallRenderer.List(found.Name, words, number, pages, result.Total, result.Items, similar);
    }

    public async Task<string> GetAsync(string? workspace, string? ids, CancellationToken ct)
    {
        var found = await guard.GetWorkspaceAsync(workspace, ct).ConfigureAwait(false);
        var wanted = FlowArgs.ParseIds(ids);
        var charLimit = Math.Max(2000, pipeline.MaxOutputChars - ReservedChars);
        var maps = await store.GetAsync(found.Id, wanted, ct).ConfigureAwait(false);
        var files = new Dictionary<string, FileView>(StringComparer.OrdinalIgnoreCase);
        var text = new StringBuilder();
        var skipped = new List<long>();
        foreach (var map in maps)
        {
            var report = await inspector.InspectAsync(found, map, files, ct).ConfigureAwait(false);
            var block = FlowRecallRenderer.Flow(report);
            if (text.Length > 0 && text.Length + block.Length > charLimit)
            {
                skipped.Add(map.Id);
                continue;
            }

            text.AppendLine(block);
        }

        var unknown = wanted.Where(id => maps.All(map => map.Id != id)).ToList();
        if (unknown.Count > 0)
        {
            text.AppendLine($"No flow with id {string.Join(", ", unknown)} in workspace '{found.Name}'. recall(action='list') shows the ids.");
        }

        if (skipped.Count > 0)
        {
            text.AppendLine($"Not included (the answer would be too long): flow {string.Join(", ", skipped)}. Ask for {(skipped.Count == 1 ? "it" : "them")} in a separate call.");
        }

        if (maps.Count > 0)
        {
            text.AppendLine(FlowRecallRenderer.Footer(found.Name));
        }

        return text.ToString().TrimEnd();
    }

    // Search by meaning is not built yet (the index of a host without it is disabled), so this returns nothing and the list is exactly what the words matched.
    private async Task<IReadOnlyList<FeatureMapSummary>> SimilarAsync(long workspaceId, string? words, IReadOnlyList<FeatureMapSummary> already, CancellationToken ct)
    {
        if (!semantic.IsEnabled || words is null)
        {
            return [];
        }

        try
        {
            var hits = await semantic.SearchAsync(workspaceId, words, SimilarTake, ct).ConfigureAwait(false);
            var shown = already.Select(item => item.Id).ToHashSet();
            var ids = hits.OrderByDescending(hit => hit.Score).Select(hit => hit.FlowId).Where(id => !shown.Contains(id)).ToList();
            if (ids.Count == 0)
            {
                return [];
            }

            var maps = await store.GetAsync(workspaceId, ids, ct).ConfigureAwait(false);
            return [.. maps.Select(map => new FeatureMapSummary(map.Id, map.Name, map.Summary, map.Steps.Count, map.Version, map.Ordered, map.UpdatedUtc))];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "The semantic search of flows failed; the list shows only what the words matched.");
            return [];
        }
    }
}
