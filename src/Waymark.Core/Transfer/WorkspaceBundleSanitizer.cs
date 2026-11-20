using Waymark.Core.Guides;
using Waymark.Core.Memory;
using Waymark.Core.Security;
using Waymark.Core.Workspaces;

namespace Waymark.Core.Transfer;

/// <summary>A bundle that holds only what this Waymark accepts, and what was left out of the file and why.</summary>
public sealed record SanitizedBundle(WorkspaceBundle Bundle, IReadOnlyList<string> Notes);

/// <summary>
/// Treats a bundle as untrusted input: the file may be edited by hand, damaged, or made by something else. Every workspace name, rule and feature map goes through
/// the same rules the admin UI and memorise apply, and whatever fails is left out with a note. Pure: no file system access, nothing is repaired or guessed.
/// </summary>
public static class WorkspaceBundleSanitizer
{
    public const int MaxWorkspaces = 100;
    public const int MaxMapsPerWorkspace = 1000;
    public const int MaxNotesPerWorkspace = 2000;

    private const int MaxNotes = 40;

    public static SanitizedBundle Sanitize(WorkspaceBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        var notes = new NoteList();
        var workspaces = new List<BundleWorkspace>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var workspace in bundle.Workspaces ?? [])
        {
            if (workspace is null)
            {
                continue;
            }

            if (workspaces.Count >= MaxWorkspaces)
            {
                notes.Add($"The file has more than {MaxWorkspaces} workspaces; the rest was left out.");
                break;
            }

            var name = workspace.Name?.Trim();
            var nameError = WorkspacePathRules.ValidateName(name);
            if (nameError is not null)
            {
                notes.Add($"A workspace was left out: {nameError}");
                continue;
            }

            if (!names.Add(name!))
            {
                notes.Add($"The workspace '{name}' is in the file twice; the second one was left out.");
                continue;
            }

            workspaces.Add(new BundleWorkspace(
                name!,
                CleanPath(workspace.PathHint),
                workspace.Enabled,
                CleanRules(name!, workspace.AccessRules, notes),
                CleanMaps(name!, workspace.FeatureMaps, notes),
                CleanInstructions(name!, workspace.Instructions, notes),
                CleanNotes(name!, workspace.Notes, notes)));
        }

        var clean = new WorkspaceBundle(WorkspaceBundleJson.FormatName, WorkspaceBundleJson.CurrentVersion, bundle.ExportedUtc, workspaces);
        return new SanitizedBundle(clean, notes.Items);
    }

    private static string? CleanPath(string? path)
    {
        var text = path?.Trim();
        return string.IsNullOrEmpty(text) || text.Length > WorkspacePathRules.MaxPathLength || text.Any(char.IsControl) ? null : text;
    }

    private static List<BundleRule> CleanRules(string workspace, IReadOnlyList<BundleRule>? rules, NoteList notes)
    {
        var clean = new List<BundleRule>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in rules ?? [])
        {
            if (rule is null)
            {
                continue;
            }

            if (clean.Count >= AccessRuleRules.MaxRulesPerWorkspace)
            {
                notes.Add($"{workspace}: more than {AccessRuleRules.MaxRulesPerWorkspace} access rules; the rest was left out.");
                break;
            }

            var pattern = string.Empty;
            string? problem = null;
            if (!Enum.IsDefined(rule.Kind))
            {
                problem = "unknown rule type";
            }
            else if (!AccessRuleRules.TryNormalize(rule.Kind, rule.Pattern, out pattern, out var error))
            {
                problem = error;
            }

            if (problem is not null)
            {
                notes.Add($"{workspace}: an access rule was left out ({problem}).");
                continue;
            }

            var note = rule.Note?.Trim() ?? string.Empty;
            if (!AccessRuleRules.IsValidNote(note))
            {
                notes.Add($"{workspace}: the access rule '{pattern}' was left out (its note is not acceptable).");
                continue;
            }

            if (seen.Add($"{rule.Kind}|{pattern}"))
            {
                clean.Add(new BundleRule(rule.Kind, pattern, note, rule.Enabled, rule.CreatedUtc));
            }
        }

        return clean;
    }

    private static List<BundleMap> CleanMaps(string workspace, IReadOnlyList<BundleMap>? maps, NoteList notes)
    {
        var clean = new List<BundleMap>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var map in maps ?? [])
        {
            if (map is null)
            {
                continue;
            }

            if (clean.Count >= MaxMapsPerWorkspace)
            {
                notes.Add($"{workspace}: more than {MaxMapsPerWorkspace} feature maps; the rest was left out.");
                break;
            }

            var cleaned = CleanMap(workspace, map, notes);
            if (cleaned is not null && names.Add(cleaned.Name))
            {
                clean.Add(cleaned);
            }
        }

        return clean;
    }

    // The same rules the Instructions page applies: one-line summary and normalized text within their limits, and no secret. Text that fails is left out whole,
    // because a cut instruction can say the opposite of the whole one.
    private static BundleInstructions? CleanInstructions(string workspace, BundleInstructions? instructions, NoteList notes)
    {
        if (instructions is null)
        {
            return null;
        }

        var summary = WorkspaceGuideRules.NormalizeSummary(instructions.Summary ?? string.Empty);
        var text = GuideRules.Normalize(instructions.Text ?? string.Empty);
        if (summary.Length == 0 && text.Length == 0)
        {
            return null;
        }

        if (summary.Length > WorkspaceGuideRules.MaxSummaryChars || text.Length > WorkspaceGuideRules.MaxInstructionsChars)
        {
            notes.Add($"{workspace}: the summary or instructions are longer than this Waymark allows; they were left out.");
            return null;
        }

        if (SensitiveDataScanner.Redact(summary).HasFindings || SensitiveDataScanner.Redact(text).HasFindings || SensitiveDataScanner.ContainsMarker(summary + "\n" + text))
        {
            notes.Add($"{workspace}: the summary or instructions look like they hold a secret or a redaction marker; they were left out.");
            return null;
        }

        return new BundleInstructions(summary, text);
    }

    // A note goes through the rules memorise and the Notes page apply: one line within the cap, a known kind and author kind, an anchor that parses, no secret.
    // One that fails is left out on its own; the others still come.
    private static List<BundleNote>? CleanNotes(string workspace, IReadOnlyList<BundleNote>? incoming, NoteList notes)
    {
        if (incoming is null)
        {
            return null;
        }

        var clean = new List<BundleNote>();
        var left = 0;
        foreach (var note in incoming)
        {
            if (note is null)
            {
                continue;
            }

            if (clean.Count >= MaxNotesPerWorkspace)
            {
                notes.Add($"{workspace}: more than {MaxNotesPerWorkspace} notes; the rest was left out.");
                break;
            }

            if (CleanNote(note) is { } cleaned)
            {
                clean.Add(cleaned);
            }
            else
            {
                left++;
            }
        }

        if (left > 0)
        {
            notes.Add($"{workspace}: {left} note(s) were left out (empty, too long, an unknown kind or author, an anchor that is not a path, or text that looks like a secret).");
        }

        return clean;
    }

    private static BundleNote? CleanNote(BundleNote note)
    {
        string body;
        try
        {
            body = NoteRules.Clean(note.Body);
        }
        catch (ArgumentException)
        {
            return null;
        }

        if (!NoteKinds.TryParse(note.Kind, out var kind) || !NoteAuthorKinds.TryParse(note.AuthorKind, out var authorKind))
        {
            return null;
        }

        var author = NoteAuthor.Of(authorKind, note.AuthorId);
        string? flow = null;
        if (!string.IsNullOrWhiteSpace(note.Flow))
        {
            if (!FeatureMapRules.TryNormalizeName(note.Flow, out var flowName))
            {
                return null;
            }

            flow = flowName;
        }

        string? detached = null;
        if (!string.IsNullOrWhiteSpace(note.DetachedFrom))
        {
            if (!FeatureMapRules.TryNormalizeName(note.DetachedFrom, out var detachedName))
            {
                return null;
            }

            detached = detachedName;
        }

        var step = CleanSpec(note.Step, allowRange: true, out var stepOk);
        var about = CleanSpec(note.About, allowRange: false, out var aboutOk);
        if (!stepOk || !aboutOk || (step is not null && flow is null && detached is null) || (about is not null && (flow is not null || detached is not null)))
        {
            return null;
        }

        var all = string.Join("\n", body, step ?? string.Empty, about ?? string.Empty, author.Id);
        if (SensitiveDataScanner.Redact(all).HasFindings || SensitiveDataScanner.ContainsMarker(all))
        {
            return null;
        }

        return new BundleNote(body, NoteKinds.ToText(kind), flow, step, about, flow is null ? detached : null, NoteAuthorKinds.ToText(author.Kind), author.Id, note.CreatedUtc, note.UpdatedUtc);
    }

    private static string? CleanSpec(string? spec, bool allowRange, out bool ok)
    {
        ok = true;
        if (string.IsNullOrWhiteSpace(spec))
        {
            return null;
        }

        if (!FeatureComponentSpec.TryParse(spec, out var component, out _) || (component.IsRange && !allowRange))
        {
            ok = false;
            return null;
        }

        return FeatureComponentSpec.Format(component);
    }

    private static BundleMap? CleanMap(string workspace, BundleMap map, NoteList notes)
    {
        if (!FeatureMapRules.TryNormalizeName(map.Name, out var name))
        {
            notes.Add($"{workspace}: a feature map was left out (its name is not 1 to 60 lowercase letters, digits and '-').");
            return null;
        }

        var summary = map.Summary?.Trim() ?? string.Empty;
        if (summary.Length > FeatureMapRules.MaxSummaryChars)
        {
            notes.Add($"{workspace}: the feature map '{name}' was left out (its summary is longer than {FeatureMapRules.MaxSummaryChars} characters).");
            return null;
        }

        return map.Ordered ? CleanFlow(workspace, name, summary, map, notes) : CleanList(workspace, name, summary, map, notes);
    }

    /// <summary>A map that is a plain list of components (an earlier version, or a map saved before flows): the components only, in order.</summary>
    private static BundleMap? CleanList(string workspace, string name, string summary, BundleMap map, NoteList notes)
    {
        var components = new List<BundleComponent>();
        var specs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var component in map.Components ?? [])
        {
            if (component is null)
            {
                continue;
            }

            if (!FeatureComponentSpec.TryParse(component.Spec, out var parsed, out var error))
            {
                notes.Add($"{workspace}: the feature map '{name}' was left out ({error})");
                return null;
            }

            var note = component.Note?.Trim() ?? string.Empty;
            if (note.Length > FeatureComponentSpec.MaxNoteChars || note.Any(char.IsControl))
            {
                notes.Add($"{workspace}: the feature map '{name}' was left out (a note is longer than {FeatureComponentSpec.MaxNoteChars} characters or has control characters).");
                return null;
            }

            var spec = FeatureComponentSpec.Format(parsed);
            if (specs.Add(spec))
            {
                components.Add(new BundleComponent(spec, note));
            }
        }

        if (components.Count == 0 || components.Count > FeatureMapRules.MaxComponentsPerMap)
        {
            notes.Add($"{workspace}: the feature map '{name}' was left out (a map holds 1 to {FeatureMapRules.MaxComponentsPerMap} components, this one has {components.Count}).");
            return null;
        }

        return new BundleMap(name, summary, map.UpdatedUtc, components, false);
    }

    /// <summary>A flow: the steps are checked with the same grammar memorise uses (one trigger, one level deeper at most per step, links known), so an import cannot bring in a flow that Claude could not save.</summary>
    private static BundleMap? CleanFlow(string workspace, string name, string summary, BundleMap map, NoteList notes)
    {
        var lines = new List<string>();
        foreach (var component in map.Components ?? [])
        {
            if (component is null)
            {
                continue;
            }

            var note = component.Note?.Trim() ?? string.Empty;
            if (note.Length > FeatureComponentSpec.MaxNoteChars || note.Any(char.IsControl))
            {
                notes.Add($"{workspace}: the feature map '{name}' was left out (a note is longer than {FeatureComponentSpec.MaxNoteChars} characters or has control characters).");
                return null;
            }

            if (!FlowLinkKinds.TryParse(component.Link, out var link) || component.Depth < 0 || component.Depth > FeatureMapRules.MaxDepth)
            {
                notes.Add($"{workspace}: the feature map '{name}' was left out (a step has a link or a depth that is not valid).");
                return null;
            }

            var line = new string(' ', component.Depth * FlowStepText.IndentWidth) + (component.Spec ?? string.Empty).Trim();
            if (link != FlowLinkKind.Call)
            {
                line += " ~" + FlowLinkKinds.ToText(link);
            }

            lines.Add(note.Length == 0 ? line : $"{line} | {note}");
        }

        if (!FlowStepText.TryParse(string.Join("\n", lines), out var steps, out var errors))
        {
            notes.Add($"{workspace}: the feature map '{name}' was left out ({errors[0]})");
            return null;
        }

        return new BundleMap(
            name,
            summary,
            map.UpdatedUtc,
            [.. steps.Select(step => new BundleComponent(step.Spec, step.Note, step.Depth, FlowLinkKinds.ToText(step.Link)))],
            true);
    }

    private sealed class NoteList
    {
        private readonly List<string> _items = [];
        private int _dropped;

        public IReadOnlyList<string> Items => _dropped == 0 ? _items : [.. _items, $"... and {_dropped} more."];

        public void Add(string note)
        {
            if (_items.Count < MaxNotes)
            {
                _items.Add(note);
            }
            else
            {
                _dropped++;
            }
        }
    }
}
