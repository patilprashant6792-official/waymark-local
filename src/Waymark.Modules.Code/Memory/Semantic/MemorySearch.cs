using Microsoft.Extensions.Logging;
using Waymark.Core.Memory;

namespace Waymark.Modules.Code.Memory.Semantic;

/// <summary>
/// Hybrid search over one workspace's flows and notes. Four legs: note words (FTS5, bm25), flow words (<see cref="FlowWordScorer"/>), and, when the owner switched it on,
/// note meaning and flow meaning (one query vector against the stored vectors). The legs are merged by reciprocal rank fusion (k = 60), then a pinned note and an exact
/// name match are lifted. Notes on a flow are grouped under that flow, with the steps they are on. Retired and replaced notes never show.
/// Any failure of the provider leaves the words legs, and the result says so.
/// </summary>
internal sealed class MemorySearch(
    INoteStore notes,
    IFeatureMapStore maps,
    IMemorySettings settings,
    QueryEmbedder queries,
    MemoryVectorCache cache,
    ILogger<MemorySearch> logger) : IMemorySearch
{
    public const string Unavailable = "search by meaning is unavailable right now";

    private const int LegTake = 50;
    private const int MaxTake = 50;
    private const double RrfK = 60;
    private const double PinnedBoost = 1.2;
    private const double ExactBoost = 1.5;

    public async Task<MemorySearchResult> SearchAsync(long workspaceId, string query, int take, CancellationToken ct)
    {
        var wanted = Math.Clamp(take, 1, MaxTake);
        var text = (query ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return new MemorySearchResult([], false, null);
        }

        var words = NoteFtsQuery.Words(text);
        var noteWords = await NoteWordsAsync(workspaceId, words, ct).ConfigureAwait(false);
        var flows = await MemoryItems.ListFlowsAsync(maps, workspaceId, ct).ConfigureAwait(false);
        var flowWords = FlowWordScorer.Rank(flows, words, LegTake);
        var meaning = await MeaningAsync(workspaceId, text, ct).ConfigureAwait(false);

        var fused = new Dictionary<(MemoryHitKind Kind, long Id), Fused>();
        Add(fused, noteWords.Select(id => (MemoryHitKind.Note, id)), byWords: true);
        Add(fused, flowWords.Select(match => (MemoryHitKind.Flow, match.FlowId)), byWords: true);
        Add(fused, meaning.Hits.Select(hit => (hit.Kind == EmbeddingItemKind.Flow ? MemoryHitKind.Flow : MemoryHitKind.Note, hit.ItemId)), byWords: false);

        var noteIds = fused.Keys.Where(key => key.Kind == MemoryHitKind.Note).Select(key => key.Id).ToList();
        var found = noteIds.Count == 0 ? new Dictionary<long, Note>() : (await notes.GetAsync(workspaceId, noteIds, ct).ConfigureAwait(false)).Where(note => note.IsActive).ToDictionary(note => note.Id);
        var flowsById = flows.ToDictionary(map => map.Id);
        var stepsByFlow = flowWords.ToDictionary(match => match.FlowId, match => match.Steps);

        // What is gone (a deleted flow, a retired note the index still had) is dropped; what is pinned or named exactly is lifted.
        foreach (var (key, entry) in fused.ToList())
        {
            if ((key.Kind == MemoryHitKind.Note && !found.ContainsKey(key.Id)) || (key.Kind == MemoryHitKind.Flow && !flowsById.ContainsKey(key.Id)))
            {
                fused.Remove(key);
                continue;
            }

            if (key.Kind == MemoryHitKind.Note && found[key.Id].Pinned)
            {
                entry.Score *= PinnedBoost;
            }

            if (key.Kind == MemoryHitKind.Flow && IsExact(flowsById[key.Id], text, words))
            {
                entry.Score *= ExactBoost;
            }
        }

        return new MemorySearchResult(Group(fused, found, stepsByFlow).Take(wanted).ToList(), meaning.Used, meaning.Notice);
    }

    private static List<MemoryHit> Group(Dictionary<(MemoryHitKind Kind, long Id), Fused> fused, Dictionary<long, Note> found, Dictionary<long, IReadOnlyList<string>> stepsByFlow)
    {
        var flowHits = new Dictionary<long, GroupedFlow>();
        var standalone = new List<MemoryHit>();
        foreach (var ((_, id), entry) in fused.Where(pair => pair.Key.Kind == MemoryHitKind.Flow))
        {
            List<string> steps = stepsByFlow.TryGetValue(id, out var matched) ? [.. matched] : [];
            flowHits[id] = new GroupedFlow(entry.Score, entry.Match, steps, []);
        }

        foreach (var ((_, id), entry) in fused.Where(pair => pair.Key.Kind == MemoryHitKind.Note).OrderByDescending(pair => pair.Value.Score))
        {
            var note = found[id];
            if (note.MapId is { } mapId && (flowHits.ContainsKey(mapId) || note.FlowName is not null))
            {
                if (!flowHits.TryGetValue(mapId, out var group))
                {
                    group = new GroupedFlow(entry.Score, entry.Match, [], []);
                    flowHits[mapId] = group;
                }

                group.Score = Math.Max(group.Score, entry.Score);
                group.Match = Combine(group.Match, entry.Match);
                group.Notes.Add(id);
                if (note.StepSpec is { } spec && !group.Steps.Contains(spec, StringComparer.OrdinalIgnoreCase))
                {
                    group.Steps.Add(spec);
                }

                continue;
            }

            standalone.Add(new MemoryHit(MemoryHitKind.Note, id, null, note.StepSpec, entry.Score, entry.Match, [], []));
        }

        var all = flowHits.Select(pair => new MemoryHit(MemoryHitKind.Flow, pair.Key, pair.Key, null, pair.Value.Score, pair.Value.Match, pair.Value.Steps, pair.Value.Notes))
            .Concat(standalone);
        return [.. all.OrderByDescending(hit => hit.Score).ThenBy(hit => hit.Kind).ThenBy(hit => hit.Id)];
    }

    private static void Add(Dictionary<(MemoryHitKind Kind, long Id), Fused> fused, IEnumerable<(MemoryHitKind Kind, long Id)> ranked, bool byWords)
    {
        var rank = 0;
        foreach (var key in ranked)
        {
            rank++;
            var score = 1.0 / (RrfK + rank);
            if (fused.TryGetValue(key, out var entry))
            {
                entry.Score += score;
                entry.Match = Combine(entry.Match, byWords ? MemoryMatch.Words : MemoryMatch.Meaning);
            }
            else
            {
                fused[key] = new Fused { Score = score, Match = byWords ? MemoryMatch.Words : MemoryMatch.Meaning };
            }
        }
    }

    private static MemoryMatch Combine(MemoryMatch a, MemoryMatch b) => a == b ? a : MemoryMatch.Both;

    // The query is the flow's name ('waymark guide delivery' or 'waymark-guide-delivery'), or one of its words is a step's member name.
    private static bool IsExact(FeatureMap map, string query, IReadOnlyList<string> words)
    {
        var flat = query.Replace(' ', '-').Trim('-');
        if (string.Equals(flat, map.Name, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return map.Steps.Any(step =>
            FeatureComponentSpec.TryParse(step.Spec, out var component, out _)
            && component.Member is { } member
            && words.Contains(member.ToLowerInvariant()));
    }

    private async Task<IReadOnlyList<long>> NoteWordsAsync(long workspaceId, IReadOnlyList<string> words, CancellationToken ct)
    {
        var all = NoteFtsQuery.Build(words, all: true);
        if (all is null)
        {
            return [];
        }

        var hits = (await notes.SearchWordsAsync(workspaceId, all, LegTake, ct).ConfigureAwait(false)).Select(hit => hit.NoteId).ToList();
        if (hits.Count < LegTake && words.Count > 1)
        {
            // Every word is precise; any word finds more. The precise ones stay first.
            var any = await notes.SearchWordsAsync(workspaceId, NoteFtsQuery.Build(words, all: false)!, LegTake, ct).ConfigureAwait(false);
            hits.AddRange(any.Select(hit => hit.NoteId).Where(id => !hits.Contains(id)));
        }

        return hits;
    }

    private async Task<MeaningLeg> MeaningAsync(long workspaceId, string query, CancellationToken ct)
    {
        MemorySettings active;
        try
        {
            active = await settings.GetAsync(ct).ConfigureAwait(false);
            if (!active.IsActive || await settings.IsExcludedAsync(workspaceId, ct).ConfigureAwait(false))
            {
                return new MeaningLeg([], false, null);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "The search-by-meaning settings could not be read; this search uses words only.");
            return new MeaningLeg([], false, Unavailable);
        }

        try
        {
            var vector = await queries.EmbedAsync(active, query, ct).ConfigureAwait(false);
            var hits = await cache.TopAsync(workspaceId, active.ModelId, vector, LegTake, ct).ConfigureAwait(false);
            return new MeaningLeg(hits, true, null);
        }
        catch (EmbeddingException ex)
        {
            logger.LogWarning("Search by meaning failed for one query ({Reason}); the answer uses words only.", ex.Message);
            return new MeaningLeg([], false, Unavailable);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Search by meaning failed for one query; the answer uses words only.");
            return new MeaningLeg([], false, Unavailable);
        }
    }

    private sealed record MeaningLeg(IReadOnlyList<VectorHit> Hits, bool Used, string? Notice);

    private sealed class Fused
    {
        public double Score { get; set; }

        public MemoryMatch Match { get; set; }
    }

    private sealed class GroupedFlow(double score, MemoryMatch match, List<string> steps, List<long> notes)
    {
        public double Score { get; set; } = score;

        public MemoryMatch Match { get; set; } = match;

        public List<string> Steps { get; } = steps;

        public List<long> Notes { get; } = notes;
    }
}
