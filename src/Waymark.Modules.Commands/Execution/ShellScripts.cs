using System.Text;

namespace Waymark.Modules.Commands.Execution;

/// <summary>A script saved to a temporary file so a shell can run it without any quoting of the text on a command line. Deleted when disposed.</summary>
public sealed class ScriptFile : IDisposable
{
    public ScriptFile(string path)
    {
        Path = path;
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            File.Delete(Path);
        }
        catch (IOException)
        {
            // Left behind in the temp folder; harmless.
        }
        catch (UnauthorizedAccessException)
        {
            // Left behind in the temp folder; harmless.
        }
    }
}

public static class ShellScripts
{
    private const string FolderName = "waymark-scripts";

    public static async Task<ScriptFile> WriteAsync(ShellInfo shell, string script, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentException.ThrowIfNullOrWhiteSpace(script);

        var folder = Path.Combine(Path.GetTempPath(), FolderName);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, Guid.NewGuid().ToString("N") + shell.FileExtension);

        // PowerShell 5 reads a script without a byte order mark as ANSI; cmd and bash need plain text.
        var encoding = shell.Kind == ShellKind.PowerShell ? new UTF8Encoding(true) : new UTF8Encoding(false);
        var text = shell.Kind == ShellKind.Bash ? script.Replace("\r\n", "\n", StringComparison.Ordinal) : script;
        if (shell.Kind == ShellKind.Cmd)
        {
            text = "@echo off\r\n" + text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);
        }

        await File.WriteAllTextAsync(path, text, encoding, ct).ConfigureAwait(false);
        return new ScriptFile(path);
    }

    public static IReadOnlyList<string> Arguments(ShellInfo shell, string scriptPath)
    {
        ArgumentNullException.ThrowIfNull(shell);
        return shell.Kind switch
        {
            ShellKind.PowerShell => ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath],
            ShellKind.Cmd => ["/d", "/q", "/c", "call", scriptPath],
            ShellKind.Bash => [scriptPath],
            _ => throw new ArgumentOutOfRangeException(nameof(shell), shell.Kind, "Unknown shell kind."),
        };
    }
}
