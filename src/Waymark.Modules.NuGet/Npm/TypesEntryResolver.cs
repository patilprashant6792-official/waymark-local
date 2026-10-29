using System.Text.Json;

namespace Waymark.Modules.NuGet.Npm;

/// <summary>
/// Finds the entry declaration files of a package from its package.json, so the symbols that a caller reaches first rank higher than the ones deep inside the package:
/// the <c>types</c> / <c>typings</c> fields, the <c>types</c> condition of <c>exports</c>, a <c>.d.ts</c> beside <c>main</c>, then <c>index.d.ts</c>.
/// </summary>
internal static class TypesEntryResolver
{
    private static readonly JsonDocumentOptions Options = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private static readonly string[] Conditions = ["types", "import", "require", "default", "node"];

    /// <summary>The entry files, relative to the package folder, with '/' separators; <c>index.d.ts</c> is always among them as a last resort.</summary>
    public static IReadOnlyList<string> Entries(string? packageJson)
    {
        var entries = new List<string>();
        if (!string.IsNullOrWhiteSpace(packageJson))
        {
            try
            {
                using var document = JsonDocument.Parse(packageJson, Options);
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    AddString(entries, root, "types");
                    AddString(entries, root, "typings");
                    if (root.TryGetProperty("exports", out var exports))
                    {
                        AddFromExports(entries, exports, 0);
                    }

                    if (root.TryGetProperty("main", out var main) && main.ValueKind == JsonValueKind.String && main.GetString() is { } mainFile)
                    {
                        var normalized = Normalize(mainFile);
                        var dot = normalized.LastIndexOf('.');
                        entries.Add(dot > 0 ? normalized[..dot] + ".d.ts" : normalized + ".d.ts");
                    }
                }
            }
            catch (JsonException)
            {
                // A package.json that does not parse still has an index.d.ts to try.
            }
        }

        entries.Add("index.d.ts");
        return entries.Where(e => e.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Whether the package.json says the package ships its own declarations.</summary>
    public static bool DeclaresTypes(string? packageJson)
    {
        if (string.IsNullOrWhiteSpace(packageJson))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(packageJson, Options);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && (root.TryGetProperty("types", out _) || root.TryGetProperty("typings", out _) || root.TryGetProperty("typesVersions", out _));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void AddString(List<string> entries, JsonElement element, string property)
    {
        if (element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { } text)
        {
            entries.Add(Normalize(text));
        }
    }

    private static void AddFromExports(List<string> entries, JsonElement exports, int depth)
    {
        if (depth > 5)
        {
            return;
        }

        switch (exports.ValueKind)
        {
            case JsonValueKind.String:
                var text = exports.GetString();
                if (text is not null && text.EndsWith(".d.ts", StringComparison.OrdinalIgnoreCase))
                {
                    entries.Add(Normalize(text));
                }

                break;
            case JsonValueKind.Object:
                // The root entry is "."; an object of conditions (types, import, require ...) is read directly.
                if (exports.TryGetProperty(".", out var root))
                {
                    AddFromExports(entries, root, depth + 1);
                    break;
                }

                foreach (var condition in Conditions)
                {
                    if (exports.TryGetProperty(condition, out var value))
                    {
                        AddFromExports(entries, value, depth + 1);
                    }
                }

                break;
        }
    }

    private static string Normalize(string path)
    {
        var normalized = path.Replace('\\', '/').Trim();
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return normalized.TrimStart('/');
    }
}
