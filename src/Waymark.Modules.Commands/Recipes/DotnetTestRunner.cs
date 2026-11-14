using System.Text.Json;

namespace Waymark.Modules.Commands.Recipes;

/// <summary>
/// Which runner 'dotnet test' uses in a folder. Since .NET 10 a global.json with "test": { "runner": "Microsoft.Testing.Platform" } switches it to
/// Microsoft.Testing.Platform, which takes a solution or project only as --solution or --project and refuses a bare path.
/// dotnet reads the first global.json at or above the folder it runs in, so this looks the same way.
/// </summary>
public static class DotnetTestRunner
{
    private const string TestingPlatform = "Microsoft.Testing.Platform";
    private const long MaxGlobalJsonBytes = 64 * 1024;

    private static readonly JsonDocumentOptions ReadOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static bool UsesTestingPlatform(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        var globalJson = FindGlobalJson(folder);
        if (globalJson is null)
        {
            return false;
        }

        try
        {
            if (new FileInfo(globalJson).Length > MaxGlobalJsonBytes)
            {
                return false;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(globalJson), ReadOptions);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("test", out var test)
                && test.ValueKind == JsonValueKind.Object
                && test.TryGetProperty("runner", out var runner)
                && runner.ValueKind == JsonValueKind.String
                && string.Equals(runner.GetString(), TestingPlatform, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // dotnet reports a damaged or unreadable global.json itself; the plain command line keeps the earlier behavior.
            return false;
        }
    }

    private static string? FindGlobalJson(string folder)
    {
        for (var current = new DirectoryInfo(Path.GetFullPath(folder)); current is not null; current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, "global.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
