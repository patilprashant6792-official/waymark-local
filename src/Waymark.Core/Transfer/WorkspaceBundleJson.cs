using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Waymark.Core.Transfer;

/// <summary>Writes and reads the text of a <see cref="WorkspaceBundle"/>. Reading never throws on bad input: it says why the text is not a bundle.</summary>
public static class WorkspaceBundleJson
{
    public const string FormatName = "waymark-workspace-bundle";
    public const int CurrentVersion = 1;

    /// <summary>The largest bundle text that is read, in characters. A bundle of hundreds of maps is well below this.</summary>
    public const int MaxChars = 5_000_000;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        MaxDepth = 12,
        Converters = { new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false) },
    };

    public static string Serialize(WorkspaceBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        return JsonSerializer.Serialize(bundle, Options);
    }

    public static bool TryParse(string? text, [NotNullWhen(true)] out WorkspaceBundle? bundle, [NotNullWhen(false)] out string? error)
    {
        bundle = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "The file is empty.";
            return false;
        }

        if (text.Length > MaxChars)
        {
            error = "The file is too large to be a Waymark bundle.";
            return false;
        }

        WorkspaceBundle? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<WorkspaceBundle>(text, Options);
        }
        catch (JsonException)
        {
            error = "The file is not a Waymark bundle: it is not valid JSON of the expected shape.";
            return false;
        }

        if (parsed is null || !string.Equals(parsed.Format, FormatName, StringComparison.Ordinal))
        {
            error = "The file is not a Waymark workspace bundle.";
            return false;
        }

        if (parsed.FormatVersion != CurrentVersion)
        {
            error = parsed.FormatVersion > CurrentVersion
                ? "The bundle was made by a newer Waymark. Update Waymark on this computer, then import it again."
                : "The bundle was made by an older Waymark that this one no longer reads.";
            return false;
        }

        bundle = parsed;
        error = null;
        return true;
    }
}
