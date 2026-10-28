using System.Text;

namespace Waymark.Modules.Commands.Recipes;

/// <summary>Work that Waymark does itself instead of starting a program (listing ports, ending a program, deleting bin and obj). Returns the exit code.</summary>
public delegate Task<int> InProcessAction(Action<string> write, CancellationToken stop);

/// <summary>
/// A checked command: the catalog operation it belongs to, the program (a name such as dotnet or git, found by absolute path before it starts) and its
/// arguments as a list. No shell ever reads these arguments. <paramref name="InProcess"/> replaces the program for the few operations Waymark does itself.
/// </summary>
/// <param name="ExecutablePath">
/// The absolute path to start instead of looking <paramref name="Program"/> up on PATH. Set only by a recipe that located the file itself: the Python of this computer, or
/// the interpreter of a project's virtual environment (a program inside the workspace, which the approval card shows). <paramref name="Program"/> is then the name shown.
/// </param>
/// <param name="WorkingDirectory">The folder to run in, inside the workspace; null for the workspace folder.</param>
public sealed record CommandPlan(
    string Operation,
    string Program,
    IReadOnlyList<string> Arguments,
    TimeSpan Timeout,
    InProcessAction? InProcess = null,
    string? Display = null,
    string? ExecutablePath = null,
    string? WorkingDirectory = null)
{
    /// <summary>The line shown to the owner and written to the console: the program and its arguments, quoted as a person would type them.</summary>
    public string CommandLine => Display ?? CommandLineText.Format(Program, Arguments);
}

public static class CommandLineText
{
    public static string Format(string program, IReadOnlyList<string> arguments)
    {
        var text = new StringBuilder(program);
        foreach (var argument in arguments)
        {
            text.Append(' ').Append(Quote(argument));
        }

        return text.ToString();
    }

    private static string Quote(string argument)
    {
        if (argument.Length > 0 && argument.All(c => !char.IsWhiteSpace(c) && c != '"' && c != '\''))
        {
            return argument;
        }

        return "\"" + argument.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }
}
