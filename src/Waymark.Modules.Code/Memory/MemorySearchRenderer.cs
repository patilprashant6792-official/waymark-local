using System.Globalization;
using System.Text;
using Waymark.Core.Memory;

namespace Waymark.Modules.Code.Memory;

/// <summary>
/// The answer of recall list with a query: ranked flows and notes, each flow with the steps that matched and the notes found under it, so the agent can often
/// go straight to code_method. Notes are one line each and labelled with their author.
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
        IReadOnlyDictionary<long, Note> notes)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(maps);
        ArgumentNullException.ThrowIfNull(notes);
        var mode = result.UsedMeaning ? "words + meaning" : result.Notice is null ? "words only" : $"words only: {result.Notice}";
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"# Memory search: workspace {workspace}, '{NoteRules.OneLine(query, 120)}' ({mode})").AppendLine();
        var number = 0;
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
                var line = NoteLines.Line(note, null, withDate: false, bodyChars: NoteChars);
                var colon = line.IndexOf(']');
                var anchor = NoteLines.Anchor(note);

                // 'note #57 convention [claude-client: x] about src/A.cs: body'
                text.AppendLine(CultureInfo.InvariantCulture, $"{number}. {line[..(colon + 1)]} {anchor}{line[(colon + 1)..]}  [{Match(hit.MatchedBy)}]");
                showedNotes = true;
            }
        }

        if (number == 0)
        {
            text.AppendLine("Nothing matched. Try other words (names of types, methods or features), or recall(action='list') without a query for every flow.");
            return text.ToString().TrimEnd();
        }

        text.AppendLine();
        if (showedNotes)
        {
            text.AppendLine(NoteRules.HintLine);
        }

        text.Append("Read a flow with recall(action='get', ids='<id>'); open a step with code_method. recall(action='list') without a query lists every flow.");
        return text.ToString();
    }

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
