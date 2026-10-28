using System.Collections.Concurrent;

namespace Waymark.Modules.Commands.Execution;

/// <summary>
/// Finds a program by its absolute path. A bare name given to the operating system can also match a file in the current folder,
/// which here is the workspace; searching only the folders of PATH (and the dotnet install) rules that out.
/// </summary>
public sealed class ExecutableLocator
{
    private const string PythonKey = "\0python";

    // Tools that people install for their own user (uv, pipx, uv tool install, cargo) land in these folders, which a service's PATH does not always list.
    private static readonly HashSet<string> UserInstalledTools = new(StringComparer.OrdinalIgnoreCase) { "uv", "uvx", "ruff", "pyright", "mypy" };

    private readonly ConcurrentDictionary<string, string?> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The full path of the program, or null when it is not installed.</summary>
    public string? Find(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var found = _cache.GetOrAdd(name, key => Locate(key, skipStoreStubs: false));
        if (found is not null && !File.Exists(found))
        {
            _cache.TryRemove(name, out _);
            return Locate(name, skipStoreStubs: false);
        }

        return found;
    }

    /// <summary>
    /// The Python of this computer: the <c>py</c> launcher or <c>python</c> on Windows, <c>python3</c> or <c>python</c> elsewhere, from the folders of PATH.
    /// The Windows Store stub (a <c>python.exe</c> in WindowsApps that opens the Store instead of running) is skipped. Null when there is none.
    /// </summary>
    public string? FindPython()
    {
        var found = _cache.GetOrAdd(PythonKey, _ => LocatePython());
        if (found is not null && !File.Exists(found))
        {
            _cache.TryRemove(PythonKey, out _);
            return LocatePython();
        }

        return found;
    }

    private static string? LocatePython()
    {
        string[] names = OperatingSystem.IsWindows() ? ["py", "python"] : ["python3", "python"];
        foreach (var name in names)
        {
            var found = Locate(name, skipStoreStubs: true);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static string? Locate(string name, bool skipStoreStubs)
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

        if (UserInstalledTools.Contains(name))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(home))
            {
                folders.Add(Path.Combine(home, ".local", "bin"));
                folders.Add(Path.Combine(home, ".cargo", "bin"));
            }
        }

        foreach (var folder in folders)
        {
            if (!Path.IsPathRooted(folder) || (skipStoreStubs && IsStoreStubFolder(folder)))
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

    private static bool IsStoreStubFolder(string folder) =>
        folder.Contains("\\WindowsApps", StringComparison.OrdinalIgnoreCase) || folder.Contains("/WindowsApps", StringComparison.OrdinalIgnoreCase);
}
