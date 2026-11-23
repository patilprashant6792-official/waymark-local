using System.Text;
using Waymark.Core.Memory;
using Waymark.Core.Security;
using Waymark.Core.Tools;

namespace Waymark.Modules.Code.Memory;

/// <summary>
/// recall: finds flows and notes (by words, and by meaning when the owner switched it on) and describes flows step by step with what the code says now,
/// with the notes anchored to them. It never returns code: the agent reads the steps it needs with code_method.
/// </summary>
internal sealed class FlowRecallService(
    IPathGuard guard,
    IFeatureMapStore store,
    INoteStore notes,
    FlowInspector inspector,
    IMemorySearch search,
    ToolPipelineOptions pipeline)
{
    // The same page size as the workspace guide's index, so page 2 continues exactly where the guide stops.
    private const int DefaultPageSize = FlowIndex.PageSize;
    private const int MaxPageSize = 50;
    private const int SearchTake = 10;
    private const int ReservedChars = 600;

    public async Task<string> ListAsync(string? workspace, string? query, int? page, int? pageSize, CancellationToken ct)
    {
        var found = await guard.GetWorkspaceAsync(workspace, ct).ConfigureAwait(false);
        var words = string.IsNullOrWhiteSpace(query) ? null : query.Trim();
        if (words is not null)
        {
            return await SearchAsync(found.Id, found.Name, words, pageSize, ct).ConfigureAwait(false);
        }

        var size = Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize);
        var number = Math.Max(1, page ?? 1);
        var result = await store.ListAsync(found.Id, null, (number - 1) * size, size, ct).ConfigureAwait(false);
        if (result.Total == 0)
        {
            return $"No flows are saved for workspace '{found.Name}' yet. After you trace a feature from its trigger to its outcome, save it with memorise(action='save_feature_map').";
        }

        var pages = Math.Max(1, (result.Total + size - 1) / size);
        return FlowRecallRenderer.List(found.Name, number, pages, result.Total, result.Items);
    }

    public async Task<string> GetAsync(string? workspace, string? ids, CancellationToken ct)
    {
        var found = await guard.GetWorkspaceAsync(workspace, ct).ConfigureAwait(false);
        var wanted = FlowArgs.ParseIds(ids);
        var charLimit = Math.Max(2000, pipeline.MaxOutputChars - ReservedChars);
        var maps = await store.GetAsync(found.Id, wanted, ct).ConfigureAwait(false);
        IReadOnlyList<Note> anchored = maps.Count == 0 ? [] : await notes.ListForFlowsAsync(found.Id, [.. maps.Select(map => map.Id)], ct).ConfigureAwait(false);
        var staleness = new Dictionary<long, NoteStaleness>();
        var files = new Dictionary<string, FileView>(StringComparer.OrdinalIgnoreCase);
        var text = new StringBuilder();
        var skipped = new List<long>();
        var showedNotes = false;
        foreach (var map in maps)
        {
            var report = await inspector.InspectAsync(found, map, files, ct).ConfigureAwait(false);
            var own = anchored.Where(note => note.MapId == map.Id).ToList();
            foreach (var note in own)
            {
                if (Staleness(report, note) is { } judged)
                {
                    staleness[note.Id] = judged;
                }
            }

            var block = FlowRecallRenderer.Flow(report, new FlowNotes(own, staleness));
            if (text.Length > 0 && text.Length + block.Length > charLimit)
            {
                skipped.Add(map.Id);
                continue;
            }

            showedNotes |= own.Count > 0;
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

        if (showedNotes)
        {
            text.AppendLine(NoteRules.HintLine);
        }

        if (maps.Count > 0)
        {
            text.AppendLine(FlowRecallRenderer.Footer(found.Name));
        }

        return text.ToString().TrimEnd();
    }

    /// <summary>
    /// Whether a note of this flow may be stale, from the report that was built anyway (no second read of any file): a step note compares its code fingerprint
    /// with the step as found now; a flow note compares the flow's version. Null when there is nothing to compare.
    /// </summary>
    internal static NoteStaleness? Staleness(FlowReport report, Note note)
    {
        if (note.StepSpec is { } spec)
        {
            if (note.CodeHash is null)
            {
                return null;
            }

            for (var i = 0; i < report.Steps.Count; i++)
            {
                if (string.Equals(report.Steps[i].Spec, spec, StringComparison.OrdinalIgnoreCase))
                {
                    var now = StepFingerprint.Of(report.Views[i]);
                    var stale = now is not null && !string.Equals(now, note.CodeHash, StringComparison.Ordinal);
                    return new NoteStaleness(note.Id, stale, stale ? "code changed" : null);
                }
            }

            return null;
        }

        if (note.FlowVersion is { } written)
        {
            var stale = written != report.Map.Version;
            return new NoteStaleness(note.Id, stale, stale ? "flow changed" : null);
        }

        return null;
    }

    private async Task<string> SearchAsync(long workspaceId, string workspaceName, string query, int? pageSize, CancellationToken ct)
    {
        var take = Math.Clamp(pageSize ?? SearchTake, 1, MaxPageSize);
        var result = await search.SearchAsync(workspaceId, query, take, ct).ConfigureAwait(false);
        var flowIds = result.Hits.Select(hit => hit.Kind == MemoryHitKind.Flow ? hit.Id : hit.FlowId).OfType<long>().Distinct().ToList();

        // The short index form (search by meaning on) shows only the notes that stand alone; notes grouped under a flow come with recall get.
        var noteIds = result.MeaningOn
            ? result.Hits.Where(hit => hit.Kind == MemoryHitKind.Note).Select(hit => hit.Id).Distinct().ToList()
            : result.Hits.SelectMany<MemoryHit, long>(hit => hit.Kind == MemoryHitKind.Note ? new[] { hit.Id } : hit.Notes).Distinct().ToList();
        IReadOnlyList<FeatureMap> maps = flowIds.Count == 0 ? [] : await store.GetAsync(workspaceId, flowIds, ct).ConfigureAwait(false);
        IReadOnlyList<Note> found = noteIds.Count == 0 ? [] : await notes.GetAsync(workspaceId, noteIds, ct).ConfigureAwait(false);
        return MemorySearchRenderer.Render(workspaceName, query, result, maps.ToDictionary(map => map.Id), found.ToDictionary(note => note.Id));
    }
}
