namespace Waymark.Modules.Commands.Execution;

public enum ShellKind
{
    PowerShell,
    Cmd,
    Bash
}

/// <param name="Id">The name Claude and the admin page use: pwsh, powershell, cmd, bash or sh.</param>
public sealed record ShellInfo(string Id, string DisplayName, string FileName, ShellKind Kind, string FileExtension);

/// <summary>Finds the shells installed on this computer. The first one found is the automatic choice: PowerShell 7, Windows PowerShell, then cmd on Windows; bash, sh, then PowerShell elsewhere.</summary>
public sealed class ShellLocator
{
    private readonly Lazy<IReadOnlyList<ShellInfo>> _available;

    public ShellLocator(ExecutableLocator executables)
    {
        ArgumentNullException.ThrowIfNull(executables);
        _available = new Lazy<IReadOnlyList<ShellInfo>>(() => Detect(executables));
    }

    /// <summary>In order of preference.</summary>
    public IReadOnlyList<ShellInfo> Available => _available.Value;

    /// <summary>The shell with that id, or the automatic choice when none is named. Null when it is not installed.</summary>
    public ShellInfo? Resolve(string? preference)
    {
        if (string.IsNullOrWhiteSpace(preference) || string.Equals(preference.Trim(), "auto", StringComparison.OrdinalIgnoreCase))
        {
            return Available.Count > 0 ? Available[0] : null;
        }

        return Available.FirstOrDefault(shell => string.Equals(shell.Id, preference.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<ShellInfo> Detect(ExecutableLocator executables)
    {
        var shells = new List<ShellInfo>();
        if (OperatingSystem.IsWindows())
        {
            var pwsh = executables.Find("pwsh") ?? ExistingFile(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
            if (pwsh is not null)
            {
                shells.Add(new ShellInfo("pwsh", "PowerShell 7", pwsh, ShellKind.PowerShell, ".ps1"));
            }

            var windowsPowerShell = ExistingFile(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            if (windowsPowerShell is not null)
            {
                shells.Add(new ShellInfo("powershell", "Windows PowerShell", windowsPowerShell, ShellKind.PowerShell, ".ps1"));
            }

            var cmd = Environment.GetEnvironmentVariable("ComSpec");
            if (string.IsNullOrWhiteSpace(cmd) || !File.Exists(cmd))
            {
                cmd = ExistingFile(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            }

            if (cmd is not null)
            {
                shells.Add(new ShellInfo("cmd", "Command Prompt", cmd, ShellKind.Cmd, ".cmd"));
            }

            var bash = ExistingFile(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe");
            if (bash is not null)
            {
                shells.Add(new ShellInfo("bash", "Git Bash", bash, ShellKind.Bash, ".sh"));
            }

            return shells;
        }

        var unixBash = executables.Find("bash");
        if (unixBash is not null)
        {
            shells.Add(new ShellInfo("bash", "bash", unixBash, ShellKind.Bash, ".sh"));
        }

        var sh = executables.Find("sh");
        if (sh is not null)
        {
            shells.Add(new ShellInfo("sh", "sh", sh, ShellKind.Bash, ".sh"));
        }

        var unixPwsh = executables.Find("pwsh");
        if (unixPwsh is not null)
        {
            shells.Add(new ShellInfo("pwsh", "PowerShell 7", unixPwsh, ShellKind.PowerShell, ".ps1"));
        }

        return shells;
    }

    private static string? ExistingFile(params string[] parts)
    {
        if (parts.Length == 0 || string.IsNullOrWhiteSpace(parts[0]))
        {
            return null;
        }

        var path = Path.Combine(parts);
        return File.Exists(path) ? path : null;
    }
}
