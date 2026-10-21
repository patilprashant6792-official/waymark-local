using System.Collections.Concurrent;

namespace Waymark.Modules.Commands.Execution;

/// <summary>
/// Finds a program by its absolute path. A bare name given to the operating system can also match a file in the current folder,
/// which here is the workspace; searching only the folders of PATH (and the dotnet install) rules that out.
/// </summary>
public sealed class ExecutableLocator
{
    private readonly ConcurrentDictionary<string, string?> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The full path of the program, or null when it is not installed.</summary>
    public string? Find(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var found = _cache.GetOrAdd(name, Locate);
        if (found is not null && !File.Exists(found))
        {
            _cache.TryRemove(name, out _);
            return Locate(name);
        }

        return found;
    }

    private static string? Locate(string name)
    {
        var fileName = OperatingSystem.IsWindows() ? name + ".exe" : name;
        var folders = new List<string>();
        if (string.Equals(name, "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
            if (!string.IsNullOrWhiteSpace(root))
            {
                folders.Add(root);
            }
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        folders.AddRange(path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (OperatingSystem.IsWindows() && string.Equals(name, "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            folders.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet"));
        }

        foreach (var folder in folders)
        {
            if (!Path.IsPathRooted(folder))
            {
                continue;
            }

            try
            {
                var candidate = Path.Combine(folder.Trim('"'), fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry.
            }
        }

        return null;
    }
}
