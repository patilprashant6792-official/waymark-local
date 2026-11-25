using System.Globalization;
using System.Text;
using Waymark.Core.Memory;

namespace Waymark.Modules.Code.Memory;

/// <summary>
/// The answer of memory_recall list with a query. With search by meaning on it is a short index: one row per flow (name, match score, id) and per note that
/// stands alone (the note's line and its score), so the answer stays small and the agent picks a flow and reads it with memory_recall get. The match score
/// is the similarity in meaning (0 to 1) and shows only on rows search by meaning found. With search by meaning off it is the words answer: ranked
/// flows and notes, each flow with the steps that matched and the notes found under it, so the agent can often go straight to code_method.
/// Notes are one line each and labelled with their author.
/// </summary>
internal static class MemorySearchRenderer
{
    private const int MaxStepsPerFlow = 3;
    private const int MaxNotesPerFlow = 3;
    private const int NoteChars = 200;

    public static string Render(
        string workspace,
        string query,
        MemorySearchResult result,
        IReadOnlyDictionary<long, FeatureMap> maps,
        IReadOnlyDictionary<long, Note> notes,
        int page = 1)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(maps);
        ArgumentNullException.ThrowIfNull(notes);
        var mode = result.UsedMeaning ? "words + meaning" : result.Notice is null ? "words only" : $"words only: {result.Notice}";
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"# Memory search: workspace {workspace}, '{NoteRules.OneLine(query, 120)}' ({mode})").AppendLine();
        if (result.Hits.Count == 0 && result.Skip > 0)
        {
            return text.AppendLine(CultureInfo.InvariantCulture, $"This page is past the last one: the query has {result.Total} results.").ToString().TrimEnd();
        }

        var body = result.MeaningOn ? Index(text, result, maps, notes) : Detailed(text, result, maps, notes);
        if (!result.HasMore)
        {
            return body;
        }

        var shown = result.Skip + result.Hits.Count;
        return body + string.Create(
            CultureInfo.InvariantCulture,
            $"\nResults {result.Skip + 1}-{shown} of {result.Total}. Next: memory_recall(action='list', query='{NoteRules.OneLine(query, 120).Replace("'", "", StringComparison.Ordinal)}', page={page + 1}).");
    }

    // Search by meaning on: one row per hit, no summaries, steps or grouped notes. memory_recall get has the path and the notes on its steps.
    private static string Index(StringBuilder text, MemorySearchResult result, IReadOnlyDictionary<long, FeatureMap> maps, IReadOnlyDictionary<long, Note> notes)
    {
        var number = result.Skip;
        var showedNotes = false;
        foreach (var hit in result.Hits)
        {
            if (hit.Kind == MemoryHitKind.Flow)
            {
                if (!maps.TryGetValue(hit.Id, out var map))
                {
                    continue;
                }

                number++;
                text.AppendLine(CultureInfo.InvariantCulture, $"{number}. {map.Name}{ScoreCell(hit.Similarity)}  id {map.Id}");
            }
            else if (notes.TryGetValue(hit.Id, out var note))
            {
                number++;
                text.AppendLine(CultureInfo.InvariantCulture, $"{number}. {NoteRow(note)}{ScoreCell(hit.Similarity)}");
                showedNotes = true;
            }
        }

        if (number == 0)
        {
            return NothingMatched(text);
        }

        text.AppendLine();
        if (showedNotes)
        {
            text.AppendLine(NoteRules.HintLine);
        }

        text.Append("The number after a name is the match score: how close it is in meaning to your words, from 0 to 1. A row without one matched your words only. ")
            .Append("Pick a flow and read its path with memory_recall(action='get', ids='<id>'); the notes on its steps come with it.");
        return text.ToString();
    }

    // Search by meaning off: each flow with its summary, the steps that matched and the notes found under it.
    private static string Detailed(StringBuilder text, MemorySearchResult result, IReadOnlyDictionary<long, FeatureMap> maps, IReadOnlyDictionary<long, Note> notes)
    {
        var number = result.Skip;
        var showedNotes = false;
        foreach (var hit in result.Hits)
        {
            if (hit.Kind == MemoryHitKind.Flow)
            {
                if (!maps.TryGetValue(hit.Id, out var map))
                {
                    continue;
                }

                number++;
                text.AppendLine(CultureInfo.InvariantCulture, $"{number}. flow {map.Name} (id {map.Id}, v{map.Version})  [{Match(hit.MatchedBy)}]");
                if (map.Summary.Length > 0)
                {
                    text.Append("   ").AppendLine(FlowArgs.Cell(map.Summary));
                }

                foreach (var spec in hit.Steps.Take(MaxStepsPerFlow))
                {
                    var index = IndexOf(map, spec);
                    var path = FeatureComponentSpec.TryParse(spec, out var component, out _) ? component.Path : spec;
                    text.AppendLine(CultureInfo.InvariantCulture, $"   step {(index < 0 ? "?" : (index + 1).ToString(CultureInfo.InvariantCulture))} {NoteEmbedText.Label(spec)}  {path}");
                }

                foreach (var id in hit.Notes.Take(MaxNotesPerFlow))
                {
                    if (notes.TryGetValue(id, out var note))
                    {
                        text.Append("   ").AppendLine(NoteLines.Line(note, null, withDate: false, bodyChars: NoteChars));
                        showedNotes = true;
                    }
                }
            }
            else if (notes.TryGetValue(hit.Id, out var note))
            {
                number++;
                text.AppendLine(CultureInfo.InvariantCulture, $"{number}. {NoteRow(note)}  [{Match(hit.MatchedBy)}]");
                showedNotes = true;
            }
        }

        if (number == 0)
        {
            return NothingMatched(text);
        }

        text.AppendLine();
        if (showedNotes)
        {
            text.AppendLine(NoteRules.HintLine);
        }

        text.Append("Read a flow with memory_recall(action='get', ids='<id>'); open a step with code_method. memory_recall(action='list') without a query lists every flow.");
        return text.ToString();
    }

    private static string NothingMatched(StringBuilder text)
    {
        text.AppendLine("Nothing matched. Try other words (names of types, methods or features), or memory_recall(action='list') without a query for every flow.");
        return text.ToString().TrimEnd();
    }

    // 'note #57 convention [agent] about src/A.cs: body'
    private static string NoteRow(Note note)
    {
        var line = NoteLines.Line(note, null, withDate: false, bodyChars: NoteChars);
        var colon = line.IndexOf(']', StringComparison.Ordinal);
        var anchor = NoteLines.Anchor(note);
        return $"{line[..(colon + 1)]} {anchor}{line[(colon + 1)..]}";
    }

    // The similarity as two decimals after two spaces, or nothing when search by meaning did not find the item.
    private static string ScoreCell(double? similarity) =>
        similarity is { } value
            ? string.Create(CultureInfo.InvariantCulture, $"  {Math.Clamp(value, 0, 1):0.00}")
            : string.Empty;

    private static int IndexOf(FeatureMap map, string spec)
    {
        for (var i = 0; i < map.Steps.Count; i++)
        {
            if (string.Equals(map.Steps[i].Spec, spec, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static string Match(MemoryMatch match) => match switch
    {
        MemoryMatch.Both => "both",
        MemoryMatch.Meaning => "meaning",
        _ => "words",
    };
}
