using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Waymark.Core.Events;
using Waymark.Core.Memory;
using Waymark.Core.Security;
using Waymark.Core.Tools;
using Waymark.Core.Workspaces;

namespace Waymark.Modules.Code.Memory;

internal sealed class FeatureMemoryService(
    IPathGuard guard,
    IFeatureMapStore store,
    FeatureComponentReader reader,
    IContentInspector inspector,
    IContextMeter meter,
    IEventBus events,
    ToolPipelineOptions pipeline,
    TimeProvider time) : IFeatureMemoryService
{
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 50;
    private const int DefaultTokens = 6000;
    private const int MinTokens = 500;
    private const int MaxTokens = 20000;
    private const int MaxIdsPerGet = 5;
    private const int MaxComponentsPerMap = 30;
    private const int MaxComponentsPerCall = 40;
    private const int MaxSummaryChars = 300;
    private const int SummaryPreviewChars = 140;
    private const int ReservedChars = 600;
    private const int MaxProblemsListed = 6;

    // 1 to 60 characters, matching the name rule told to the agent.
    private static readonly Regex NameText = new("^[a-z0-9][a-z0-9-]{0,59}$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public async Task<string> ListAsync(string? workspace, string? query, int? page, int? pageSize, CancellationToken ct)
    {
        var found = await guard.GetWorkspaceAsync(workspace, ct).ConfigureAwait(false);
        var size = Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize);
        var number = Math.Max(1, page ?? 1);
        var words = string.IsNullOrWhiteSpace(query) ? null : query.Trim();
        var result = await store.ListAsync(found.Id, words, (number - 1) * size, size, ct).ConfigureAwait(false);
        if (result.Total == 0)
        {
            return words is null
                ? $"No feature maps are saved for workspace '{found.Name}' yet. After you explore a feature, save what you needed with memorise(action='save_feature_map')."
                : $"No feature map matches '{words}' in workspace '{found.Name}'. recall(action='list') without a query shows all of them.";
        }

        var pages = (result.Total + size - 1) / size;
        var filter = words is null ? string.Empty : $", matching '{words}'";
        var text = new StringBuilder();
        text.AppendLine($"# Feature maps: workspace {found.Name}{filter}").AppendLine();
        text.AppendLine($"{result.Total} found, page {number} of {pages}.").AppendLine();
        if (result.Items.Count == 0)
        {
            text.AppendLine("This page is past the last one.");
        }
        else
        {
            text.AppendLine("| id | name | components | summary | updated |");
            text.AppendLine("|---|---|---|---|---|");
            foreach (var item in result.Items)
            {
                var updated = item.UpdatedUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                text.AppendLine($"| {item.Id} | {item.Name} | {item.ComponentCount} | {Cell(item.Summary)} | {updated} |");
            }
        }

        text.AppendLine().Append($"Read maps with recall(action='get', ids='12,15') using the ids above (at most {MaxIdsPerGet} per call).");
        if (number < pages)
        {
            text.Append($" More: page={number + 1}.");
        }

        return text.ToString();
    }

    public async Task<string> GetAsync(string? workspace, string? ids, int? maxTokens, CancellationToken ct)
    {
        var found = await guard.GetWorkspaceAsync(workspace, ct).ConfigureAwait(false);
        var wanted = ParseIds(ids);
        var budget = Math.Clamp(maxTokens ?? DefaultTokens, MinTokens, MaxTokens);
        var charLimit = Math.Max(2000, pipeline.MaxOutputChars - ReservedChars);
        var maps = await store.GetAsync(found.Id, wanted, ct).ConfigureAwait(false);
        var text = new StringBuilder();
        var cache = new Dictionary<string, FileView>(StringComparer.OrdinalIgnoreCase);
        var used = 0;
        foreach (var map in maps)
        {
            used += await AppendMapAsync(text, found, map, cache, budget - used, charLimit, ct).ConfigureAwait(false);
        }

        var unknown = wanted.Where(id => maps.All(map => map.Id != id)).ToList();
        if (unknown.Count > 0)
        {
            text.AppendLine($"No feature map with id {string.Join(", ", unknown)} in workspace '{found.Name}'. recall(action='list') shows the ids.");
        }

        if (maps.Count > 0)
        {
            text.AppendLine($"_About {used} of {budget} tokens used._");
        }

        return text.ToString().TrimEnd();
    }

    public async Task<string> SaveAsync(string? workspace, string? name, string? summary, string? components, string? remove, CancellationToken ct)
    {
        var found = await guard.GetWorkspaceAsync(workspace, ct).ConfigureAwait(false);
        var cleanName = CleanName(name);
        var cleanSummary = CleanSummary(summary);
        var add = ParseComponents(components);
        var removeSpecs = ParseRemove(remove);
        if (add.Count == 0 && removeSpecs.Count == 0 && cleanSummary is null)
        {
            throw new ArgumentException("Nothing to save: give components, and optionally a summary or components to remove.");
        }

        var everything = string.Join("\n", cleanName, cleanSummary ?? string.Empty, components ?? string.Empty, remove ?? string.Empty);
        var findings = await inspector.InspectAsync(everything, ct).ConfigureAwait(false);
        if (findings.Count > 0)
        {
            throw new ArgumentException("The text looks like it holds a secret or a redaction marker, so nothing was saved. Take it out and call again.");
        }

        // Only what is added has to exist on this branch. What is already stored stays, whatever branch it came from.
        var cache = new Dictionary<string, FileView>(StringComparer.OrdinalIgnoreCase);
        var problems = new List<string>();
        foreach (var line in add)
        {
            var view = await reader.ReadAsync(found, line.Component, cache, ct).ConfigureAwait(false);
            if (view.Problem is not null)
            {
                problems.Add($"- `{FeatureComponentSpec.Format(line.Component)}`: {view.Problem}");
            }
        }

        if (problems.Count > 0)
        {
            throw new ArgumentException(
                $"Nothing was saved: {problems.Count} component(s) are not in the code on this branch.\n{string.Join("\n", problems.Take(MaxProblemsListed))}\nFix those names (each message lists what the file has) and call again.");
        }

        var items = add.Select(line => new FeatureMapComponent(FeatureComponentSpec.Format(line.Component), line.Note)).ToList();
        var result = await store.SaveAsync(found.Id, cleanName, cleanSummary, items, removeSpecs, MaxComponentsPerMap, time.GetUtcNow(), ct).ConfigureAwait(false);
        await events.PublishAsync(new FeatureMapsChanged(found.Id)).ConfigureAwait(false);
        var overlaps = (await store.FindOverlapsAsync(found.Id, result.Id, ct).ConfigureAwait(false)).Where(o => o.Shared * 2 >= result.Total).ToList();

        var message = new StringBuilder();
        message.Append(result.Created ? "Saved new" : "Updated");
        message.Append($" feature map '{result.Name}' (id {result.Id}) in workspace '{found.Name}': {result.Added} added, {result.Updated} notes changed, {result.Removed} removed; {result.Total} components in all.");
        message.Append($"\nRead it back with recall(action='get', ids='{result.Id}').");
        foreach (var overlap in overlaps)
        {
            message.Append($"\nNote: '{overlap.Name}' (id {overlap.Id}) shares {overlap.Shared} of these {result.Total} components. If it is the same feature, keep one map and delete the other with memorise(action='delete_feature_map', name='{overlap.Name}').");
        }

        return message.ToString();
    }

    public async Task<string> DeleteAsync(string? workspace, string? name, CancellationToken ct)
    {
        var found = await guard.GetWorkspaceAsync(workspace, ct).ConfigureAwait(false);
        var cleanName = CleanName(name);
        if (!await store.DeleteAsync(found.Id, cleanName, ct).ConfigureAwait(false))
        {
            throw new KeyNotFoundException($"No feature map named '{cleanName}' in workspace '{found.Name}'. recall(action='list') shows the names.");
        }

        await events.PublishAsync(new FeatureMapsChanged(found.Id)).ConfigureAwait(false);
        return $"Deleted feature map '{cleanName}' from workspace '{found.Name}'.";
    }

    // Reads every component of a map from the code now; returns the tokens used by what was included.
    private async Task<int> AppendMapAsync(
        StringBuilder text,
        Workspace workspace,
        FeatureMap map,
        Dictionary<string, FileView> cache,
        int tokensLeft,
        int charLimit,
        CancellationToken ct)
    {
        var views = new List<(ComponentView View, string Note)>();
        foreach (var stored in map.Components)
        {
            if (FeatureComponentSpec.TryParse(stored.Spec, out var component, out _))
            {
                views.Add((await reader.ReadAsync(workspace, component, cache, ct).ConfigureAwait(false), stored.Note));
            }
            else
            {
                var unreadable = new FeatureComponent(stored.Spec, null, null, null, null);
                views.Add((new ComponentView(unreadable, null, "the saved text is not a valid component", false), stored.Note));
            }
        }

        var away = views.Where(v => v.View.Body is null && !v.View.TooLong).ToList();
        text.AppendLine($"## Feature map '{map.Name}' (id {map.Id})").AppendLine();
        if (map.Summary.Length > 0)
        {
            text.AppendLine(map.Summary).AppendLine();
        }

        var here = views.Count - away.Count;
        var tail = away.Count == 0 ? "." : $"; {away.Count} {(away.Count == 1 ? "is" : "are")} kept in memory but not available here (listed at the end).";
        text.AppendLine($"{here} of {views.Count} components {(here == 1 ? "is" : "are")} in the code on this branch{tail}").AppendLine();

        var used = 0;
        var notIncluded = views.Where(v => v.View.TooLong).Select(v => v.View.Component).ToList();
        foreach (var (view, note) in views.Where(v => v.View.Body is not null))
        {
            var block = Block(view.Component, note, view.Body!);
            var tokens = meter.CountTokens(block);
            if (tokens > tokensLeft - used || text.Length + block.Length > charLimit)
            {
                notIncluded.Add(view.Component);
                continue;
            }

            text.Append(block);
            used += tokens;
        }

        if (away.Count > 0)
        {
            text.AppendLine("**Not available here (kept in memory):**");
            foreach (var (view, _) in away)
            {
                text.AppendLine($"- `{FeatureComponentSpec.Format(view.Component)}`: {view.Problem}");
            }

            text.AppendLine();
        }

        if (notIncluded.Count > 0)
        {
            text.AppendLine("**Not included (over the budget; read them with the call shown):**");
            foreach (var component in notIncluded)
            {
                text.AppendLine($"- `{FeatureComponentSpec.Format(component)}`: {Hint(workspace.Name, component)}");
            }

            text.AppendLine();
        }

        return used;
    }

    private static string Block(FeatureComponent component, string note, string body)
    {
        var flags = new List<string>();
        if (note.Length > 0)
        {
            flags.Add(note);
        }

        if (component.IsRange)
        {
            flags.Add("saved line range: the lines may have shifted since");
        }

        var suffix = flags.Count == 0 ? string.Empty : " - " + string.Join("; ", flags);
        return $"### {FeatureComponentSpec.Format(component)}{suffix}\n{body}\n\n";
    }

    private static string Hint(string workspace, FeatureComponent component)
    {
        if (component.IsRange)
        {
            return $"code_read('{workspace}', '{component.Path}', startLine={component.StartLine}, endLine={component.EndLine})";
        }

        if (component.IsFile)
        {
            return $"code_outline('{workspace}', '{component.Path}') for a code file, or code_read('{workspace}', '{component.Path}', startLine, endLine) for another file";
        }

        return component.Type is null
            ? $"code_outline('{workspace}', '{component.Path}') for a type, or code_method('{workspace}', '{component.Path}', '{component.Member}') for a member"
            : $"code_method('{workspace}', '{component.Path}', '{component.Member}', className='{component.Type}')";
    }

    private static string Cell(string text)
    {
        var flat = text.Replace('|', '/').Replace('\n', ' ').Replace('\r', ' ').Trim();
        return flat.Length <= SummaryPreviewChars ? flat : flat[..SummaryPreviewChars] + "...";
    }

    private static string CleanName(string? name)
    {
        var text = (name ?? string.Empty).Trim().ToLowerInvariant().Replace(' ', '-').Replace('_', '-');
        return NameText.IsMatch(text)
            ? text
            : throw new ArgumentException("name must be 1 to 60 characters: lowercase letters, digits and '-', starting with a letter or digit (for example 'command-gates').");
    }

    private static string? CleanSummary(string? summary)
    {
        var text = summary?.Trim();
        return text is { Length: > MaxSummaryChars }
            ? throw new ArgumentException($"summary is at most {MaxSummaryChars} characters: one or two sentences on what the feature does.")
            : text;
    }

    private static List<long> ParseIds(string? ids)
    {
        var parts = (ids ?? string.Empty).Trim('[', ']').Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result = new List<long>();
        foreach (var part in parts)
        {
            if (!long.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id < 1)
            {
                throw new ArgumentException($"ids are numbers from recall(action='list'), separated by commas: '{part}' is not one.");
            }

            if (!result.Contains(id))
            {
                result.Add(id);
            }
        }

        return result.Count switch
        {
            0 => throw new ArgumentException("ids is required: take the ids from recall(action='list')."),
            > MaxIdsPerGet => throw new ArgumentException($"ids accepts at most {MaxIdsPerGet} per call."),
            _ => result
        };
    }

    private static List<string> SplitLines(string? text) =>
        (text ?? string.Empty).Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static List<ComponentLine> ParseComponents(string? text)
    {
        var lines = SplitLines(text);
        if (lines.Count > MaxComponentsPerCall)
        {
            throw new ArgumentException($"components accepts at most {MaxComponentsPerCall} lines per call.");
        }

        var bySpec = new Dictionary<string, ComponentLine>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        var errors = new List<string>();
        foreach (var line in lines)
        {
            if (!FeatureComponentSpec.TryParseLine(line, out var parsed, out var error))
            {
                errors.Add(error ?? "Not a component.");
                continue;
            }

            var key = FeatureComponentSpec.Format(parsed.Component);
            if (!bySpec.ContainsKey(key))
            {
                bySpec[key] = parsed;
                order.Add(key);
            }
            else if (parsed.Note.Length > 0)
            {
                bySpec[key] = parsed;
            }
        }

        return errors.Count > 0
            ? throw new ArgumentException(string.Join(" ", errors.Take(MaxProblemsListed)))
            : order.Select(key => bySpec[key]).ToList();
    }

    private static List<string> ParseRemove(string? text)
    {
        var result = new List<string>();
        var errors = new List<string>();
        foreach (var line in SplitLines(text))
        {
            if (FeatureComponentSpec.TryParseLine(line, out var parsed, out var error))
            {
                result.Add(FeatureComponentSpec.Format(parsed.Component));
            }
            else
            {
                errors.Add(error ?? "Not a component.");
            }
        }

        return errors.Count > 0 ? throw new ArgumentException("remove: " + string.Join(" ", errors.Take(MaxProblemsListed))) : result;
    }
}
