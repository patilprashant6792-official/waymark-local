using System.Globalization;
using System.Text;
using Waymark.Core.Memory;

namespace Waymark.Modules.Code.Memory;

/// <summary>
/// The text of recall: a flow as steps with their place in the code, signature and link status, and the table of flows. It never contains code.
/// The wording of the states is the same one the owner's page uses (Here, Not on this branch, and what the link says).
/// </summary>
internal static class FlowRecallRenderer
{
    private const int SignatureChars = 90;

    public static string Flow(FlowReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var map = report.Map;
        var text = new StringBuilder();
        text.AppendLine($"## Flow '{map.Name}' (id {map.Id}, v{map.Version})").AppendLine();
        if (map.Summary.Length > 0)
        {
            text.AppendLine(map.Summary).AppendLine();
        }

        if (map.DistinctReason.Length > 0)
        {
            text.AppendLine($"Kept next to another flow with the same trigger: {map.DistinctReason}").AppendLine();
        }

        text.AppendLine(Counts(report)).AppendLine();
        for (var i = 0; i < report.Steps.Count; i++)
        {
            text.AppendLine(Line(report, i));
        }

        if (!map.Ordered)
        {
            text.AppendLine().AppendLine(
                $"This map was saved before flows existed, so it is an unordered list. When you use it, put the steps in call order under one trigger (or split it by trigger) and save it with memorise(expectedVersion={map.Version}).");
        }

        return text.ToString();
    }

    /// <summary>How to read a step and how to change the flow; once per answer, not per step.</summary>
    public static string Footer(string workspace) =>
        $"Read a step with code_method('{workspace}', path, member, className=type), or code_outline for a file. " +
        "After a change that alters the path, save the whole flow again with memorise(expectedVersion=the v shown). A step marked not on this branch is kept: do not remove it for that reason.";

    public static string List(string workspace, string? query, int page, int pages, int total, IReadOnlyList<FeatureMapSummary> items, IReadOnlyList<FeatureMapSummary> similar)
    {
        var filter = string.IsNullOrWhiteSpace(query) ? string.Empty : $", matching '{query}'";
        var text = new StringBuilder();
        text.AppendLine($"# Flows: workspace {workspace}{filter}").AppendLine();
        text.AppendLine($"{total} found, page {page} of {pages}.").AppendLine();
        if (items.Count == 0)
        {
            text.AppendLine("This page is past the last one.");
        }
        else
        {
            Table(text, items);
        }

        if (similar.Count > 0)
        {
            text.AppendLine().AppendLine("Close in meaning to the query:").AppendLine();
            Table(text, similar);
        }

        text.AppendLine().Append($"Read flows with recall(action='get', ids='12,15') using the ids above (at most {FlowArgs.MaxIdsPerGet} per call). A method name as the query finds the flows it is a step of.");
        if (page < pages)
        {
            text.Append($" More: page={page + 1}.");
        }

        return text.ToString();
    }

    private static void Table(StringBuilder text, IReadOnlyList<FeatureMapSummary> items)
    {
        text.AppendLine("| id | name | steps | version | summary | updated |");
        text.AppendLine("|---|---|---|---|---|---|");
        foreach (var item in items)
        {
            var updated = item.UpdatedUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var version = item.Ordered ? $"v{item.Version}" : $"v{item.Version} unordered";
            text.AppendLine($"| {item.Id} | {item.Name} | {item.StepCount} | {version} | {FlowArgs.Cell(item.Summary)} | {updated} |");
        }
    }

    private static string Counts(FlowReport report) => Counts(report.Steps.Count, report.Inspection, report.Map.Ordered);

    /// <summary>One line on how many steps there are, how the links stand and how many steps are not in the code here. Also the first line of the reply to a save.</summary>
    public static string Counts(int steps, StepsInspection inspection, bool ordered)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        var away = inspection.Views.Count(view => !view.Found);
        var awayText = away == 0 ? "All steps are in the code here." : $"{away} not on this branch (kept).";
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
            line.Append("  NOT ON THIS BRANCH (kept): ").Append(view.Problem);
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
