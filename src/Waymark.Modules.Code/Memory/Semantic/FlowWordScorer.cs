using Waymark.Core.Memory;
using Waymark.Core.Text;

namespace Waymark.Modules.Code.Memory.Semantic;

/// <summary>How well a flow's words match a query, and which of its steps matched.</summary>
internal sealed record FlowWordMatch(long FlowId, double Score, IReadOnlyList<string> Steps);

/// <summary>
/// The words leg of the search for flows. Pure: a query word matches a word of the flow when that word starts with it (two letters or more), or equals it.
/// Weights: the flow's name 3, a step's Type.Member 2, its path 1, the summary 1, a step label 1. The total is scaled by how many of the query's words matched,
/// so a flow that has all of them beats one that repeats one of them.
/// </summary>
internal static class FlowWordScorer
{
    private const double NameWeight = 3;
    private const double StepWeight = 2;
    private const double PathWeight = 1;
    private const double SummaryWeight = 1;
    private const double LabelWeight = 1;

    public static IReadOnlyList<FlowWordMatch> Rank(IReadOnlyList<FeatureMap> maps, IReadOnlyList<string> words, int take)
    {
        ArgumentNullException.ThrowIfNull(maps);
        ArgumentNullException.ThrowIfNull(words);
        if (words.Count == 0)
        {
            return [];
        }

        return [.. maps
            .Select(map => Score(map, words))
            .Where(match => match.Score > 0)
            .OrderByDescending(match => match.Score)
            .ThenBy(match => match.FlowId)
            .Take(Math.Max(0, take))];
    }

    public static FlowWordMatch Score(FeatureMap map, IReadOnlyList<string> words)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(words);
        var name = Tokens(map.Name);
        var summary = Tokens(map.Summary + " " + map.DistinctReason);
        var steps = map.Steps.Select(step => (Step: step, Member: Tokens(NoteEmbedText.Label(step.Spec)), Path: Tokens(PathOf(step.Spec)), Label: Tokens(step.Note))).ToList();
        var total = 0.0;
        var matchedWords = 0;
        var matchedSteps = new List<string>();
        foreach (var word in words)
        {
            var score = 0.0;
            if (Matches(name, word))
            {
                score += NameWeight;
            }

            if (Matches(summary, word))
            {
                score += SummaryWeight;
            }

            foreach (var (step, member, path, label) in steps)
            {
                var stepScore = (Matches(member, word) ? StepWeight : 0) + (Matches(path, word) ? PathWeight : 0) + (Matches(label, word) ? LabelWeight : 0);
                if (stepScore > 0)
                {
                    score += stepScore;
                    if (Matches(member, word) && !matchedSteps.Contains(step.Spec, StringComparer.OrdinalIgnoreCase))
                    {
                        matchedSteps.Add(step.Spec);
                    }
                }
            }

            if (score > 0)
            {
                matchedWords++;
                total += Math.Min(score, NameWeight + SummaryWeight + (StepWeight * 2));
            }
        }

        var coverage = (double)matchedWords / words.Count;
        return new FlowWordMatch(map.Id, total * coverage * coverage, matchedSteps);
    }

    private static HashSet<string> Tokens(string? text) =>
        new(IdentifierWords.Tokenize((text ?? string.Empty).Replace('/', ' ').Replace('-', ' ')), StringComparer.Ordinal);

    private static bool Matches(HashSet<string> tokens, string word) =>
        tokens.Contains(word) || (word.Length >= 2 && tokens.Any(token => token.StartsWith(word, StringComparison.Ordinal)));

    private static string PathOf(string spec) =>
        FeatureComponentSpec.TryParse(spec, out var component, out _) ? component.Path : spec;
}
