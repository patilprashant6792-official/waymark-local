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
                CleanMaps(name!, workspace.FeatureMaps, notes)));
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
