using System.Text.Json;
using System.Text.RegularExpressions;

namespace Waymark.Modules.Commands.Recipes;

/// <summary>
/// The options JSON of one command, read with strict types. Every key that the recipe asks for is remembered, so a key Claude sent that
/// the recipe never asked for is reported (a misspelt option must not be dropped silently).
/// </summary>
public sealed class RecipeOptions
{
    private readonly Dictionary<string, JsonElement> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly SortedSet<string> _asked = new(StringComparer.OrdinalIgnoreCase);

    /// <exception cref="ArgumentException">The text is not a JSON object.</exception>
    public static RecipeOptions Parse(string? json)
    {
        var options = new RecipeOptions();
        if (string.IsNullOrWhiteSpace(json))
        {
            return options;
        }

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("optionsJson must be a JSON object such as {\"configuration\":\"Release\"}.");
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!options._values.TryAdd(property.Name, property.Value.Clone()))
                {
                    throw new ArgumentException($"Option '{property.Name}' is given twice.");
                }
            }
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"optionsJson is not valid JSON: {ex.Message}");
        }

        return options;
    }

    public string? Text(string key, int maxLength = 200, bool multiline = false)
    {
        _asked.Add(key);
        if (!_values.TryGetValue(key, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw Invalid(key, "a string");
        }

        var text = (value.GetString() ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return null;
        }

        if (text.Length > maxLength)
        {
            throw new ArgumentException($"Option '{key}' is longer than {maxLength} characters.");
        }

        if (text.Any(c => char.IsControl(c) && !(multiline && c is '\n' or '\r' or '\t')))
        {
            throw new ArgumentException($"Option '{key}' contains control characters.");
        }

        return text;
    }

    public string RequiredText(string key, int maxLength = 200, bool multiline = false) =>
        Text(key, maxLength, multiline) ?? throw new ArgumentException($"Option '{key}' is required.");

    /// <summary>A value that must match an allow-list pattern. Null when the option is absent.</summary>
    public string? Token(string key, Regex pattern, string description)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        var text = Text(key);
        if (text is null)
        {
            return null;
        }

        return Matches(pattern, text) ? text : throw new ArgumentException($"Option '{key}' is not accepted: {description}");
    }

    public string RequiredToken(string key, Regex pattern, string description) =>
        Token(key, pattern, description) ?? throw new ArgumentException($"Option '{key}' is required.");

    /// <summary>One of the listed words, compared ignoring case. Null when the option is absent.</summary>
    public string? OneOf(string key, params string[] allowed)
    {
        var text = Text(key, 50);
        if (text is null)
        {
            return null;
        }

        return allowed.FirstOrDefault(a => string.Equals(a, text, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"Option '{key}' must be one of: {string.Join(", ", allowed)}.");
    }

    public bool Flag(string key, bool fallback = false)
    {
        _asked.Add(key);
        if (!_values.TryGetValue(key, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return fallback;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw Invalid(key, "true or false"),
        };
    }

    public int? Number(string key, int min, int max)
    {
        _asked.Add(key);
        if (!_values.TryGetValue(key, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
        {
            throw Invalid(key, "a whole number");
        }

        return number >= min && number <= max ? number : throw new ArgumentException($"Option '{key}' must be between {min} and {max}.");
    }

    public IReadOnlyList<string> List(string key, int maxItems, int maxLength = 300)
    {
        _asked.Add(key);
        if (!_values.TryGetValue(key, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            throw Invalid(key, "an array of strings");
        }

        var items = new List<string>();
        foreach (var element in value.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String)
            {
                throw Invalid(key, "an array of strings");
            }

            var text = (element.GetString() ?? string.Empty).Trim();
            if (text.Length == 0 || text.Length > maxLength || text.Any(char.IsControl))
            {
                throw new ArgumentException($"Option '{key}' has an empty, too long or malformed entry.");
            }

            items.Add(text);
        }

        return items.Count <= maxItems ? items : throw new ArgumentException($"Option '{key}' has more than {maxItems} entries.");
    }

    /// <exception cref="ArgumentException">A key was sent that the recipe did not ask for.</exception>
    public void EnsureNoUnknown()
    {
        var unknown = _values.Keys.Where(k => !_asked.Contains(k)).Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (unknown.Count > 0)
        {
            throw new ArgumentException($"Unknown option(s): {string.Join(", ", unknown)}. This action accepts: {string.Join(", ", _asked)}.");
        }
    }

    private static bool Matches(Regex pattern, string text)
    {
        try
        {
            return pattern.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static ArgumentException Invalid(string key, string expected) => new($"Option '{key}' must be {expected}.");
}
