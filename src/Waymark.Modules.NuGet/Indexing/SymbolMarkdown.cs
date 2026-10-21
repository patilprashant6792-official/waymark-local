using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Waymark.Core.NuGetIndex;
using Waymark.Core.Tools;
using Waymark.Modules.NuGet.Loading;

namespace Waymark.Modules.NuGet.Indexing;

/// <summary>One symbol the caller asked nuget_expand for, with the sections to print. <see cref="Hit"/> is null when it was not found.</summary>
internal sealed record ExpandItem(string Identifier, NuGetSymbolHit? Hit, IReadOnlySet<string> Sections);

/// <summary>Renders the answers of the nuget_* tools as compact markdown, each bounded by an output budget.</summary>
internal static partial class SymbolMarkdown
{
    private const int DescriptionChars = 120;

    public static string Packages(string query, IReadOnlyList<PackageSearchHit> hits, int maxChars)
    {
        var output = new OutputBudget(maxChars);
        output.TryAppendLine($"# NuGet packages for '{query}'");
        if (hits.Count == 0)
        {
            output.TryAppendLine();
            output.TryAppendLine("No packages found. Try fewer or different words.");
            return output.Build();
        }

        foreach (var hit in hits)
        {
            var downloads = hit.Downloads is { } count ? $" \u00b7 {Downloads(count)} downloads" : string.Empty;
            var description = SymbolFormatting.Truncate(OneLine(hit.Description), DescriptionChars);
            output.TryAppendLine($"- **{hit.Id}** {hit.Version}{downloads}" + (description.Length > 0 ? " \u2014 " + description : string.Empty));
        }

        output.TryAppendLine();
        output.TryAppendLine("Next: `nuget_find(packageId, query)` to locate a type or method, or `nuget_outline(packageId)` for the namespace map.");
        return output.Build();
    }

    public static string Outline(
        NuGetPackageOutline outline, IReadOnlyList<NuGetNamespaceCount> shown, int matching, string? prefix, TimeSpan age, int maxChars)
    {
        var package = outline.Package;
        var output = new OutputBudget(maxChars);
        output.TryAppendLine(Header(package.Key));
        output.TryAppendLine(string.Create(CultureInfo.InvariantCulture,
            $"{package.TypeCount} types \u00b7 {package.SymbolCount} indexed symbols \u00b7 {outline.Namespaces.Count} namespace(s) \u00b7 indexed {Ago(age)}"));
        output.TryAppendLine();

        if (shown.Count == 0)
        {
            output.TryAppendLine($"No namespace starts with '{prefix}'. Call `nuget_outline` without `namespace` to see them all.");
            return output.Build();
        }

        foreach (var item in shown)
        {
            output.TryAppendLine(string.Create(CultureInfo.InvariantCulture, $"- {item.Namespace} ({item.TypeCount} types)"));
        }

        if (matching > shown.Count)
        {
            output.TryAppendLine(string.Create(CultureInfo.InvariantCulture,
                $"- \u2026 {matching - shown.Count} more (pass `namespace` as a prefix filter)"));
        }

        output.TryAppendLine();
        output.TryAppendLine("This is a map, not a listing. Next: `nuget_find(packageId, query, namespace)` to locate a type or method.");
        return output.Build();
    }

    public static string Find(NuGetPackageKey key, IReadOnlyList<NuGetSymbolHit> shown, int total, int maxChars)
    {
        var output = new OutputBudget(maxChars);
        output.TryAppendLine(string.Create(CultureInfo.InvariantCulture, $"{Header(key)} \u2014 {total} match(es)"));
        output.TryAppendLine();

        if (shown.Count == 0)
        {
            output.TryAppendLine("Nothing matched. Try fewer or different words, drop `kind`, `namespace` or `declaringType`, or call `nuget_outline` to see the namespaces.");
            return output.Build();
        }

        foreach (var hit in shown)
        {
            output.TryAppendLine($"- `{hit.Sid}` {hit.Card}");
        }

        output.TryAppendLine();
        if (total > shown.Count)
        {
            output.TryAppendLine(string.Create(CultureInfo.InvariantCulture, $"Showing {shown.Count} of {total}, ranked by relevance \u00d7 prominence."));
        }

        output.TryAppendLine("Next: `nuget_expand(packageId, identifiers)` with these ids (up to 5) for signatures, properties and docs.");
        return output.Build();
    }

    public static string Expand(NuGetPackageKey key, IReadOnlyList<ExpandItem> items, int maxChars)
    {
        var output = new OutputBudget(maxChars);
        output.TryAppendLine(Header(key));
        foreach (var item in items)
        {
            output.TryAppendLine();
            if (item.Hit is null)
            {
                output.TryAppendLine($"## `{item.Identifier}` \u2014 not found in this package. Use `nuget_find` to locate it.");
                continue;
            }

            AppendSymbol(output, item.Hit, item.Sections);
        }

        return output.Build();
    }

    private static void AppendSymbol(OutputBudget output, NuGetSymbolHit hit, IReadOnlySet<string> sections)
    {
        output.TryAppendLine($"## {hit.FullName} ({KindLabel(hit.Kind)}) `{hit.Sid}`");
        var doc = sections.Contains(NuGetSections.Doc) ? ParseDoc(Section(hit, NuGetSections.Doc)) : null;
        if (doc?.Summary is { Length: > 0 } summary)
        {
            output.TryAppendLine(summary);
        }

        var signatureTitle = hit.Kind == NuGetSymbolKind.MethodGroup ? "Signatures" : "Constructors";
        AppendBlock(output, signatureTitle, hit, NuGetSections.Sig, sections);
        AppendBlock(output, "Methods", hit, NuGetSections.Methods, sections);
        AppendBlock(output, "Properties", hit, NuGetSections.Props, sections);
        AppendBlock(output, hit.Kind == NuGetSymbolKind.Enum ? "Values" : "Fields & events", hit, NuGetSections.Data, sections);

        if (doc is not null)
        {
            AppendDocTail(output, doc);
        }

        if (sections.Contains(NuGetSections.Rel))
        {
            AppendRelated(output, Section(hit, NuGetSections.Rel));
        }
    }

    private static void AppendBlock(OutputBudget output, string title, NuGetSymbolHit hit, string section, IReadOnlySet<string> sections)
    {
        var text = sections.Contains(section) ? Section(hit, section) : string.Empty;
        if (text.Length == 0)
        {
            return;
        }

        output.TryAppendLine($"**{title}**");
        output.TryAppendLine("```csharp");
        foreach (var line in text.Split('\n'))
        {
            if (!output.TryAppendLine(line))
            {
                return;
            }
        }

        output.TryAppendLine("```");
    }

    private static void AppendDocTail(OutputBudget output, DocText doc)
    {
        if (doc.Parameters.Count > 0)
        {
            output.TryAppendLine("**Parameters**");
            foreach (var (name, text) in doc.Parameters)
            {
                output.TryAppendLine($"- `{name}` {text}");
            }
        }

        if (doc.Returns is { Length: > 0 } returns)
        {
            output.TryAppendLine($"**Returns** {returns}");
        }

        if (doc.Exceptions.Count > 0)
        {
            output.TryAppendLine("**Throws**");
            foreach (var (type, text) in doc.Exceptions)
            {
                output.TryAppendLine(text.Length > 0 ? $"- `{type}` {text}" : $"- `{type}`");
            }
        }
    }

    private static void AppendRelated(OutputBudget output, string related)
    {
        var pairs = related.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .Select(parts => $"{parts[0]} `{parts[1]}`")
            .ToList();
        if (pairs.Count > 0)
        {
            output.TryAppendLine($"Related: {string.Join(", ", pairs)} \u2014 pass these ids straight to `nuget_expand`.");
        }
    }

    private static DocText? ParseDoc(string json)
    {
        if (json.Length == 0)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return new DocText(
                Text(root, "summary"),
                Text(root, "returns"),
                Map(root, "params"),
                Map(root, "exceptions"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    private static List<KeyValuePair<string, string>> Map(JsonElement root, string name)
    {
        var result = new List<KeyValuePair<string, string>>();
        if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object)
        {
            result.AddRange(value.EnumerateObject()
                .Where(p => p.Value.ValueKind == JsonValueKind.String)
                .Select(p => new KeyValuePair<string, string>(p.Name, p.Value.GetString() ?? string.Empty)));
        }

        return result;
    }

    private static string Section(NuGetSymbolHit hit, string name) => hit.Sections.GetValueOrDefault(name) ?? string.Empty;

    private static string Header(NuGetPackageKey key) => $"# {key.PackageId} {key.Version} ({key.TargetFramework})";

    private static string KindLabel(NuGetSymbolKind kind) =>
        kind == NuGetSymbolKind.MethodGroup ? "method" : kind.ToString().ToLowerInvariant();

    private static string OneLine(string text) => Whitespace().Replace(text, " ").Trim();

    private static string Downloads(long count) => count switch
    {
        >= 1_000_000_000 => (count / 1_000_000_000d).ToString("0.#", CultureInfo.InvariantCulture) + "B",
        >= 1_000_000 => (count / 1_000_000d).ToString("0.#", CultureInfo.InvariantCulture) + "M",
        >= 1_000 => (count / 1_000d).ToString("0.#", CultureInfo.InvariantCulture) + "K",
        _ => count.ToString(CultureInfo.InvariantCulture)
    };

    private static string Ago(TimeSpan age) => age switch
    {
        { TotalMinutes: < 1 } => "just now",
        { TotalHours: < 1 } => string.Create(CultureInfo.InvariantCulture, $"{(int)age.TotalMinutes} min ago"),
        { TotalHours: < 48 } => string.Create(CultureInfo.InvariantCulture, $"{(int)age.TotalHours} h ago"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{(int)age.TotalDays} days ago")
    };

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Whitespace();

    private sealed record DocText(
        string Summary,
        string Returns,
        List<KeyValuePair<string, string>> Parameters,
        List<KeyValuePair<string, string>> Exceptions);
}
