using System.Globalization;
using System.Text;
using Waymark.Core.Memory;

namespace Waymark.Modules.Code.Memory;

/// <summary>One part of a flow as memory_recall shows it. <paramref name="Next"/> is the 0-based index of the first step left for the next part, or null when the flow is complete.</summary>
internal sealed record FlowPart(string Text, int? Next);
/// <summary>
/// The text of memory_recall: a flow as steps with their place in the code, signature and link status, and the table of flows. It never contains code.
/// The wording of the states is the same one the owner's page uses (Here, Not on this branch, and what the link says).
/// </summary>
internal static class FlowRecallRenderer
{
    private const int SignatureChars = 90;

    /// <param name="notes">The notes of this flow (and maybe others; only this flow's are used), or null for none.</param>
    public static string Flow(FlowReport report, FlowNotes? notes = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        return Part(report, notes, 0, report.Steps.Count, int.MaxValue).Text;
    }

    /// <summary>
    /// The steps <paramref name="from"/> (0-based) up to <paramref name="to"/> (exclusive) of a flow, as many as fit <paramref name="maxChars"/> (at least one).
    /// The summary and the flow's own notes come only with the first part. When steps are left, the part ends with the call that continues it,
    /// and <see cref="FlowPart.Next"/> is the 0-based index of the first step not shown. The ids of the notes written are added to <see cref="FlowNotes.Shown"/>.
    /// </summary>
    public static FlowPart Part(FlowReport report, FlowNotes? notes, int from, int to, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(report);
        var map = report.Map;
        var total = report.Steps.Count;
        from = Math.Clamp(from, 0, Math.Max(0, total - 1));
        to = Math.Clamp(to, from, total);
        var partial = from > 0 || to < total;
        notes ??= FlowNotes.Empty;
        List<Note> own = [.. notes.Notes.Where(note => note.MapId == map.Id && note.IsActive)];
        var budget = new NoteBudget(NoteRules.MaxNoteCharsPerFlow);
        var text = new StringBuilder();
        text.AppendLine(from == 0 ? $"## Flow '{map.Name}' (id {map.Id}, v{map.Version})" : $"## Flow '{map.Name}' (id {map.Id}, v{map.Version}), continued from step {from + 1}").AppendLine();
        if (from == 0)
        {
            if (map.Summary.Length > 0)
            {
                text.AppendLine(map.Summary).AppendLine();
            }

            if (map.DistinctReason.Length > 0)
            {
                text.AppendLine($"Kept next to another flow with the same trigger: {map.DistinctReason}").AppendLine();
            }

            var flowNotes = own.Where(note => note.StepSpec is null).ToList();
            if (flowNotes.Count > 0)
            {
                foreach (var note in flowNotes)
                {
                    if (budget.Add(text, "    ", note, notes))
                    {
                        notes.Shown.Add(note.Id);
                    }
                }

                text.AppendLine();
            }
        }

        text.AppendLine(partial ? PartCounts(report) : Counts(report)).AppendLine();
        var next = (int?)null;
        for (var i = from; i < to; i++)
        {
            var block = new StringBuilder();
            var shown = new List<long>();
            block.AppendLine(Line(report, i));
            var step = report.Steps[i];
            var stepNotes = own.Where(note => string.Equals(note.StepSpec, step.Spec, StringComparison.OrdinalIgnoreCase)).ToList();
            var indent = new string(' ', 6 + (step.Depth * FlowStepText.IndentWidth));
            for (var n = 0; n < stepNotes.Count; n++)
            {
                if (n >= NoteRules.MaxNotesPerStepShown)
                {
                    budget.Hidden += stepNotes.Count - n;
                    break;
                }

                if (budget.Add(block, indent, stepNotes[n], notes))
                {
                    shown.Add(stepNotes[n].Id);
                }
            }

            if (i > from && text.Length + block.Length + ContinueReserve > maxChars)
            {
                next = i;
                break;
            }

            text.Append(block);
            notes.Shown.AddRange(shown);
        }

        if (next is null && to < total)
        {
            next = to;
        }

        if (next is { } resume)
        {
            text.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"Steps {from + 1}-{resume} of {total} shown. Continue: memory_recall(action='get', ids='{map.Id}', from={resume + 1})");
            return new FlowPart(text.ToString(), resume);
        }

        var specs = new HashSet<string>(report.Steps.Select(step => step.Spec), StringComparer.OrdinalIgnoreCase);
        var orphans = own.Where(note => note.StepSpec is not null && !specs.Contains(note.StepSpec)).ToList();
        if (orphans.Count > 0)
        {
            text.AppendLine().AppendLine("Notes on steps no longer in this flow:");
            foreach (var note in orphans)
            {
                if (budget.Add(text, "    ", note, notes))
                {
                    notes.Shown.Add(note.Id);
                }
            }
        }

        if (budget.Hidden > 0)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"    +{budget.Hidden} more: memory_recall(action='notes', ids='{map.Id}')");
        }

        if (!map.Ordered)
        {
            text.AppendLine().AppendLine(
                $"This map was saved before flows existed, so it is an unordered list. When you use it, put the steps in call order under one trigger (or split it by trigger) and save it with memory_save(expectedVersion={map.Version}).");
        }

        return new FlowPart(text.ToString(), null);
    }

    // Room kept for the line that tells how to continue.
    private const int ContinueReserve = 160;

    /// <summary>Counts the note lines of one flow against its character budget; what does not fit is counted as hidden.</summary>
    private sealed class NoteBudget(int chars)
    {
        private int _left = chars;

        public int Hidden { get; set; }

        /// <summary>Writes the note's line when it fits the budget; false (and counted as hidden) when it does not.</summary>
        public bool Add(StringBuilder text, string indent, Note note, FlowNotes notes)
        {
            var line = NoteLines.Line(note, notes.StalenessOf(note.Id));
            if (line.Length > _left)
            {
                Hidden++;
                return false;
            }

            _left -= line.Length;
            text.Append(indent).AppendLine(line);
            return true;
        }
    }

    /// <summary>How to read a step and how to change the flow; once per answer, not per step.</summary>
    public static string Footer(string workspace) =>
        $"Read a step with code_method('{workspace}', path, member, className=type), or code_outline for a file. " +
        "After a change that alters the path, save the whole flow again with memory_save(expectedVersion=the v shown). " +
        "A step marked NOT ON THIS BRANCH is kept for the branch that has it: do not remove it for that reason. A step marked GONE was last seen on this branch: repair the flow.";

    /// <summary>
    /// One page of the flow index (memory_recall list without a query): name and id per flow, newest first, the same lines and order as the workspace guide.
    /// memory_recall get describes a flow; nothing else of it is loaded here.
    /// </summary>
    public static string List(string workspace, int page, int pages, int total, IReadOnlyList<FeatureMapSummary> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"# Flows: workspace {workspace}").AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"{total} saved, newest first, page {page} of {pages}.").AppendLine();
        if (items.Count == 0)
        {
            text.AppendLine("This page is past the last one.");
        }
        else
        {
            foreach (var item in items)
            {
                text.Append("- ").AppendLine(FlowIndex.Line(item.Id, item.Name));
            }
        }

        text.AppendLine().Append(CultureInfo.InvariantCulture, $"Read flows with memory_recall(action='get', ids='12,15') using the ids above (at most {FlowArgs.MaxIdsPerGet} per call). memory_recall(action='list', query='...') searches flows and notes; a method name as the query finds the flows it is a step of.");
        if (page < pages)
        {
            text.Append(CultureInfo.InvariantCulture, $" More: page={page + 1}.");
        }

        return text.ToString();
    }

    private static string Counts(FlowReport report)
    {
        var text = Counts(report.Steps.Count, report.Inspection, report.Map.Ordered);
        var gone = Enumerable.Range(0, report.Steps.Count).Count(i => report.Place(i) == StepPlace.Gone);
        return gone == 0 ? text : $"{text} Of those not here, {gone} are GONE from this branch: repair the flow.";
    }

    // A flow read in parts: the totals, and the links only of the steps that were checked.
    private static string PartCounts(FlowReport report) =>
        $"{report.Steps.Count} steps in all; it comes in parts. Each step shown is checked against the code here, with its link from the step above.";

    /// <summary>One line on how many steps there are, how the links stand and how many steps are not in the code here. Also the first line of the reply to a save.</summary>
    public static string Counts(int steps, StepsInspection inspection, bool ordered)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        var away = inspection.Views.Count(view => view.Inspected && !view.Found);
        var awayText = away == 0 ? "All steps are in the code here." : $"{away} not in the code here.";
        if (!ordered)
        {
            return $"{steps} steps (unordered list). {awayText}";
        }

        var states = inspection.Links.Where(link => link is not null).Select(link => link!.State).ToList();
        int Count(FlowLinkState state) => states.Count(s => s == state);
        return $"{steps} steps. Links: {Count(FlowLinkState.Calls)} call it, {Count(FlowLinkState.NameOnly)} by name only, " +
            $"{Count(FlowLinkState.Declared)} declared, {Count(FlowLinkState.DoesNotCall)} do not call. {awayText}";
    }

    private static string Line(FlowReport report, int index)
    {
        var step = report.Steps[index];
        var view = report.Views[index];
        var line = new StringBuilder();
        line.Append(CultureInfo.InvariantCulture, $"{index + 1,2}. ").Append(' ', step.Depth * FlowStepText.IndentWidth).Append(step.Spec);
        if (!view.Found)
        {
            line.Append(report.Place(index) switch
            {
                StepPlace.Gone => "  GONE (repair): ",
                StepPlace.NotInspected => "  not checked in this part: ",
                _ => "  NOT ON THIS BRANCH (kept): ",
            }).Append(view.Problem ?? "not looked at");
            if (report.Moved[index] is { } moved)
            {
                line.Append("  moved? -> ").Append(moved);
            }
        }
        else
        {
            line.Append("  ").Append(Place(view));
            var label = LinkLabel(report, index);
            if (label.Length > 0)
            {
                line.Append("  ").Append(label);
            }

            if (view.Signature is { Length: > 0 } signature)
            {
                line.Append("  ").Append(Shorten(signature));
            }

            if (view.Overloads > 1)
            {
                line.Append(CultureInfo.InvariantCulture, $" (+{view.Overloads - 1} overloads)");
            }
        }

        if (step.Note.Length > 0)
        {
            line.Append("  | ").Append(step.Note);
        }

        return line.ToString();
    }

    private static string Place(StepView view) => view.Kind switch
    {
        StepKind.Lines => $"lines {view.StartLine}-{view.EndLine} (may have shifted since it was saved)",
        StepKind.File => $"file, {view.EndLine} lines",
        _ => $"L{view.StartLine}-{view.EndLine}",
    };

    private static string LinkLabel(FlowReport report, int index)
    {
        if (index == 0)
        {
            return report.Map.Ordered ? "[trigger]" : string.Empty;
        }

        if (report.Links[index] is not { } link)
        {
            return string.Empty;
        }

        return link.State switch
        {
            FlowLinkState.Calls => "[calls it]",
            FlowLinkState.NameOnly => "[by name only]",
            FlowLinkState.Declared => $"[declared: {link.Detail}]",
            FlowLinkState.DoesNotCall => $"[DOES NOT CALL: {link.Detail}]",
            _ => "[link not judged: not on this branch]",
        };
    }

    private static string Shorten(string text)
    {
        var flat = text.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return flat.Length <= SignatureChars ? flat : flat[..(SignatureChars - 3)] + "...";
    }
}
