using System.Diagnostics.CodeAnalysis;

namespace Waymark.Core.Memory;

/// <summary>
/// How a step is reached from the step above it. Only <see cref="Call"/> is something the code index can check (a method that calls another);
/// the others are links that syntax cannot show, so the agent declares them and they are reported as declared.
/// </summary>
public enum FlowLinkKind
{
    /// <summary>The step above calls this one directly.</summary>
    Call,

    /// <summary>An HTTP request from the step above reaches this one.</summary>
    Http,

    /// <summary>An event, message or callback from the step above reaches this one.</summary>
    Event,

    /// <summary>A user action in the interface reaches this one.</summary>
    Ui,

    /// <summary>Dependency injection or a registration connects them; there is no direct call to find.</summary>
    Di,

    /// <summary>A scheduled or background job reaches this one.</summary>
    Job,
}

/// <summary>The text forms of <see cref="FlowLinkKind"/>: lower case in storage, with a '~' in front inside a step line.</summary>
public static class FlowLinkKinds
{
    public static string ToText(FlowLinkKind kind) => kind switch
    {
        FlowLinkKind.Http => "http",
        FlowLinkKind.Event => "event",
        FlowLinkKind.Ui => "ui",
        FlowLinkKind.Di => "di",
        FlowLinkKind.Job => "job",
        _ => "call",
    };

    /// <summary>Reads "call", "http", "event", "ui", "di" or "job" in any letter case.</summary>
    public static bool TryParse(string? text, out FlowLinkKind kind)
    {
        switch ((text ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "call":
                kind = FlowLinkKind.Call;
                return true;
            case "http":
                kind = FlowLinkKind.Http;
                return true;
            case "event":
                kind = FlowLinkKind.Event;
                return true;
            case "ui":
                kind = FlowLinkKind.Ui;
                return true;
            case "di":
                kind = FlowLinkKind.Di;
                return true;
            case "job":
                kind = FlowLinkKind.Job;
                return true;
            default:
                kind = FlowLinkKind.Call;
                return false;
        }
    }

    /// <summary>The stored text, or <see cref="FlowLinkKind.Call"/> when it is not a known kind.</summary>
    public static FlowLinkKind FromStored(string? text) => TryParse(text, out var kind) ? kind : FlowLinkKind.Call;
}

/// <summary>
/// One step of a flow: a place in the code, how deep it is (0 is the trigger), how it is reached from its parent, and why it matters.
/// <paramref name="Spec"/> is the canonical text of the component; <paramref name="Valid"/> is false for stored text that is no longer a valid component.
/// </summary>
public sealed record FlowStep(FeatureComponent Component, int Depth, FlowLinkKind Link, string Note, string Spec, bool Valid = true);

/// <summary>
/// The text form of a flow, one step per line. Pure. Shared by memorise (Claude), the import of a bundle (the owner) and the pages, so all of them accept the same flows.
/// <code>
/// src/Orders/OrderEndpoint.cs:OrderEndpoint.Post                | the trigger
///   src/Orders/OrderService.cs:OrderService.Place               | totals are computed here
///     src/Orders/OrderRepository.cs:OrderRepository.Insert
///   src/Orders/Mail.cs:Mailer.Send ~event                      | sent after the order is stored
/// </code>
/// Two spaces of indent mean "called by the step above". The first line is the only one at depth 0.
/// </summary>
public static class FlowStepText
{
    public const int IndentWidth = 2;
    private const int MaxErrors = 6;

    /// <summary>
    /// Reads the steps of a flow. False with the reasons (at most six) when the text is not a valid flow; nothing is guessed or repaired.
    /// </summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out IReadOnlyList<FlowStep>? steps, out IReadOnlyList<string> errors)
    {
        steps = null;
        var problems = new List<string>();
        var result = new List<FlowStep>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var previousDepth = -1;
        var lines = (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var raw = lines[index];
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var number = index + 1;
            var indent = 0;
            while (indent < raw.Length && raw[indent] == ' ')
            {
                indent++;
            }

            if (indent < raw.Length && raw[indent] == '\t')
            {
                problems.Add($"Line {number}: indent with {IndentWidth} spaces per level, not a tab.");
                continue;
            }

            if (indent % IndentWidth != 0)
            {
                problems.Add($"Line {number}: indent with {IndentWidth} spaces per level, this line has {indent}.");
                continue;
            }

            if (!TryReadLine(raw[indent..], number, out var component, out var link, out var linkGiven, out var note, out var error))
            {
                problems.Add(error!);
                continue;
            }

            var depth = indent / IndentWidth;
            var spec = FeatureComponentSpec.Format(component!);
            if (depth > FeatureMapRules.MaxDepth)
            {
                problems.Add($"Line {number}: a flow is at most {FeatureMapRules.MaxDepth} levels deep. Split the flow, or leave out the detail below this step.");
            }
            else if (result.Count == 0 && depth != 0)
            {
                problems.Add($"Line {number}: the first line is the trigger and has no indent.");
            }
            else if (result.Count > 0 && depth == 0)
            {
                problems.Add($"Line {number}: a flow has one trigger. Indent '{Shorten(spec)}' under the step that calls it, or save it as its own flow.");
            }
            else if (depth > previousDepth + 1)
            {
                problems.Add($"Line {number}: '{Shorten(spec)}' is indented {depth - previousDepth} levels below the line above. Indent a step one level under the step that calls it.");
            }
            else if (depth == 0 && linkGiven)
            {
                problems.Add($"Line {number}: the trigger has no incoming link, so it takes no ~{FlowLinkKinds.ToText(link)}.");
            }
            else if (!seen.Add(spec))
            {
                problems.Add($"Line {number}: '{Shorten(spec)}' is already a step of this flow. List a step once and say in a note where else it is used.");
            }
            else
            {
                result.Add(new FlowStep(component!, depth, link, note, spec));
                previousDepth = depth;
                continue;
            }

            // A refused line still sets the depth, so one bad line does not produce a second error for every line below it.
            previousDepth = Math.Min(depth, FeatureMapRules.MaxDepth);
        }

        if (result.Count == 0 && problems.Count == 0)
        {
            problems.Add("A flow needs at least one step.");
        }

        if (result.Count > FeatureMapRules.MaxComponentsPerMap)
        {
            problems.Add($"A flow holds at most {FeatureMapRules.MaxComponentsPerMap} steps and this one has {result.Count}. Leave out detail, or split it by outcome.");
        }

        errors = problems.Take(MaxErrors).ToList();
        if (problems.Count > MaxErrors)
        {
            errors = [.. errors, $"And {problems.Count - MaxErrors} more."];
        }

        if (problems.Count > 0)
        {
            return false;
        }

        steps = result;
        return true;
    }

    /// <summary>The canonical text of the steps: what the page copies and what a bundle carries. <see cref="TryParse"/> reads it back.</summary>
    public static string Format(IReadOnlyList<FlowStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        return string.Join("\n", steps.Select(FormatLine));
    }

    public static string FormatLine(FlowStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        var line = new string(' ', step.Depth * IndentWidth) + step.Spec;
        if (step.Link != FlowLinkKind.Call)
        {
            line += " ~" + FlowLinkKinds.ToText(step.Link);
        }

        return step.Note.Length == 0 ? line : $"{line} | {step.Note}";
    }

    /// <summary>The step as it is stored, in the order and depth it has.</summary>
    public static IReadOnlyList<FeatureMapStep> ToStored(IReadOnlyList<FlowStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        return [.. steps.Select(step => new FeatureMapStep(step.Spec, step.Depth, FlowLinkKinds.ToText(step.Link), step.Note))];
    }

    /// <summary>
    /// The stored steps as steps. Text that is no longer a valid component comes back with <see cref="FlowStep.Valid"/> false and is reported, not dropped.
    /// </summary>
    public static IReadOnlyList<FlowStep> FromStored(IReadOnlyList<FeatureMapStep> stored)
    {
        ArgumentNullException.ThrowIfNull(stored);
        var steps = new List<FlowStep>(stored.Count);
        foreach (var item in stored)
        {
            var link = FlowLinkKinds.FromStored(item.Link);
            if (FeatureComponentSpec.TryParse(item.Spec, out var component, out _))
            {
                steps.Add(new FlowStep(component, item.Depth, link, item.Note, FeatureComponentSpec.Format(component)));
            }
            else
            {
                steps.Add(new FlowStep(new FeatureComponent(item.Spec, null, null, null, null), item.Depth, link, item.Note, item.Spec, Valid: false));
            }
        }

        return steps;
    }

    /// <summary>The index of the step this one is called by, or null for the trigger.</summary>
    public static int? ParentIndex(IReadOnlyList<FlowStep> steps, int index)
    {
        ArgumentNullException.ThrowIfNull(steps);
        if (index <= 0 || index >= steps.Count)
        {
            return null;
        }

        var depth = steps[index].Depth;
        for (var i = index - 1; i >= 0; i--)
        {
            if (steps[i].Depth < depth)
            {
                return i;
            }
        }

        return null;
    }

    /// <summary>True when the step after this one is indented under it.</summary>
    public static bool HasChildren(IReadOnlyList<FlowStep> steps, int index)
    {
        ArgumentNullException.ThrowIfNull(steps);
        return index >= 0 && index + 1 < steps.Count && steps[index + 1].Depth > steps[index].Depth;
    }

    private static bool TryReadLine(
        string body,
        int number,
        out FeatureComponent? component,
        out FlowLinkKind link,
        out bool linkGiven,
        out string note,
        out string? error)
    {
        component = null;
        link = FlowLinkKind.Call;
        linkGiven = false;
        note = string.Empty;
        error = null;
        var text = body.Trim();
        if (text.StartsWith("- ", StringComparison.Ordinal) || text.StartsWith("* ", StringComparison.Ordinal))
        {
            text = text[2..].Trim();
        }

        var bar = text.IndexOf('|', StringComparison.Ordinal);
        var head = (bar < 0 ? text : text[..bar]).Trim();
        note = bar < 0 ? string.Empty : text[(bar + 1)..].Trim();
        if (note.Length > FeatureComponentSpec.MaxNoteChars || note.Any(char.IsControl))
        {
            error = $"Line {number}: a note is at most {FeatureComponentSpec.MaxNoteChars} characters and has no control characters.";
            return false;
        }

        var words = head.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var specWords = new List<string>();
        foreach (var word in words)
        {
            if (word.StartsWith('~'))
            {
                if (linkGiven || !FlowLinkKinds.TryParse(word[1..], out link))
                {
                    error = $"Line {number}: '{word}' is not a link. Write one of ~http, ~event, ~ui, ~di or ~job after the step, or nothing for a direct call.";
                    return false;
                }

                linkGiven = true;
                continue;
            }

            specWords.Add(word.Trim('`'));
        }

        if (specWords.Count != 1)
        {
            error = $"Line {number}: write one step per line, 'path:Type.Member', then an optional ~link and an optional '| note'.";
            return false;
        }

        if (!FeatureComponentSpec.TryParse(specWords[0], out component, out var specError))
        {
            error = $"Line {number}: {specError}";
            return false;
        }

        return true;
    }

    private static string Shorten(string text) => text.Length <= 60 ? text : text[..57] + "...";
}
