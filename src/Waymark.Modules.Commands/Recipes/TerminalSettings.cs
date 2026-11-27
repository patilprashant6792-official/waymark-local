using System.Text;
using System.Text.RegularExpressions;

namespace Waymark.Modules.Commands.Recipes;

/// <summary>
/// What Claude said about the terminal a command runs in: its name, how to tell the program is up (a text it prints or a port it listens on), how long
/// to wait for that, whether to replace what already runs there, and an optional most it may run.
/// </summary>
public sealed record TerminalSettings(string? Name, string? ReadyPattern, int? ReadyPort, int ReadyTimeoutSeconds, bool Replace, int? MaxMinutes)
{
    public const int DefaultReadyTimeoutSeconds = 120;

    /// <summary>The option keys every command that keeps running accepts.</summary>
    public static readonly IReadOnlyList<string> Keys = ["terminal", "readyPattern", "readyPort", "readyTimeoutSeconds", "replace", "timeoutMinutes"];

    /// <summary>Reads the terminal options of a command that keeps running. <c>timeoutMinutes</c> is its most lifetime here (default none, at most a week).</summary>
    public static TerminalSettings Read(RecipeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new TerminalSettings(
            options.Token("terminal", TerminalNames.Pattern(), "a terminal name: lower-case letters, digits and '-', up to 32"),
            options.Text("readyPattern", 200),
            options.Number("readyPort", 1, 65535),
            options.Number("readyTimeoutSeconds", 5, 600) ?? DefaultReadyTimeoutSeconds,
            options.Flag("replace"),
            options.Number("timeoutMinutes", 1, 10_080));
    }
}

/// <summary>The names of terminals: lower case, letters, digits and '-', 1 to 32 characters, starting with a letter or digit.</summary>
public static partial class TerminalNames
{
    public const int MaxLength = 32;

    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{0,31}$", RegexOptions.None, 100)]
    public static partial Regex Pattern();

    /// <summary>A valid name made from any text, such as a folder name ("My.Api" becomes "my-api"); <paramref name="fallback"/> when nothing is left.</summary>
    public static string From(string? text, string fallback)
    {
        var result = new StringBuilder();
        foreach (var c in (text ?? string.Empty).ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                result.Append(c);
            }
            else if (result.Length > 0 && result[^1] != '-')
            {
                result.Append('-');
            }

            if (result.Length >= MaxLength)
            {
                break;
            }
        }

        var name = result.ToString().Trim('-');
        return name.Length == 0 ? fallback : name;
    }

    /// <summary>The name a project folder gives its terminal: the folder's own name.</summary>
    public static string ForFolder(string folder, string fallback)
    {
        var trimmed = (folder ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return From(Path.GetFileName(trimmed), fallback);
    }
}

/// <summary>The text each ecosystem's dev servers print when they are up, when the app inside a watcher went down, and when it came back.</summary>
public static class ReadySignals
{
    public static readonly IReadOnlyList<string> DotnetReady = ["Now listening on:", "Application started."];

    public static readonly IReadOnlyList<string> DotnetWatchDown = ["Exited with error code", "Waiting for a file to change", "Build failed"];

    public static readonly IReadOnlyList<string> DotnetWatchUp = ["Now listening on:", "Application started."];

    public static readonly IReadOnlyList<string> NodeReady =
    [
        "Local:", "ready in", "Ready in", "Compiled successfully", "compiled successfully", "listening on", "Listening on", "Server running at",
        "started server on", "Server is running",
    ];

    public static readonly IReadOnlyList<string> NodeDown = ["Failed to compile", "EADDRINUSE", "app crashed - waiting for file changes"];

    public static readonly IReadOnlyList<string> NodeUp = ["Compiled successfully", "compiled successfully", "restarting due to changes"];

    public static readonly IReadOnlyList<string> PythonReady =
    [
        "Uvicorn running on", "Application startup complete", "Running on http", "Starting development server at", "Listening at:", "Serving on",
    ];

    public static readonly IReadOnlyList<string> PythonDown = ["Error loading ASGI app", "Exception in worker process"];

    public static readonly IReadOnlyList<string> PythonUp = ["Application startup complete", "Started server process"];
}
