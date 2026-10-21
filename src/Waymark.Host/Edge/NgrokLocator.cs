namespace Waymark.Host.Edge;

/// <summary>Finds an executable without starting a shell: the configured path first, then PATH, then the folders the Windows installers use.</summary>
public sealed class NgrokLocator(NgrokOptions options)
{
    public string? Find()
    {
        if (options.ExecutablePath is { } configured)
        {
            return File.Exists(configured) ? configured : null;
        }

        return FindExecutable("ngrok");
    }

    public static string? FindExecutable(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var fileName = OperatingSystem.IsWindows() ? name + ".exe" : name;

        var folders = new List<string>();
        folders.AddRange((Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        // A program installed a moment ago is on the machine's PATH but not on the PATH this process started with.
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            folders.Add(Path.Combine(localAppData, "Microsoft", "WindowsApps"));
            folders.Add(Path.Combine(localAppData, "Microsoft", "WinGet", "Links"));
        }

        foreach (var folder in folders)
        {
            try
            {
                var candidate = Path.Combine(folder, fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry is skipped.
            }
        }

        return null;
    }
}
