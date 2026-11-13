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

    // macOS ships these in /usr/bin as placeholders: without the Xcode Command Line Tools, running one opens an install dialog and fails.
    private static readonly HashSet<string> XcodePlaceholders = new(StringComparer.Ordinal) { "git", "python3", "pip3" };

    private static readonly Lazy<bool> DeveloperToolsInstalled = new(FindDeveloperTools);

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
    /// The Windows Store stub (a <c>python.exe</c> in WindowsApps that opens the Store instead of running) and the macOS placeholder
    /// (<c>/usr/bin/python3</c> without the Xcode Command Line Tools) are skipped. Null when there is none.
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

    /// <summary>
    /// The <c>npm-cli.js</c> that belongs to a node: <c>node_modules/npm/bin/npm-cli.js</c> beside node.exe (Windows) or under <c>../lib</c> (Linux, macOS).
    /// npm is started as <c>node npm-cli.js</c>, never as the npm.cmd shim, because a .cmd file is read by cmd.exe and would interpret its arguments.
    /// </summary>
    public string? FindNpmCli(string nodePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodePath);
        var folder = Path.GetDirectoryName(nodePath);
        if (folder is null)
        {
            return null;
        }

        string[] candidates =
        [
            Path.Combine(folder, "node_modules", "npm", "bin", "npm-cli.js"),
            Path.GetFullPath(Path.Combine(folder, "..", "lib", "node_modules", "npm", "bin", "npm-cli.js")),
        ];
        return candidates.FirstOrDefault(File.Exists);
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

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (UserInstalledTools.Contains(name) && !string.IsNullOrWhiteSpace(home))
        {
            folders.Add(Path.Combine(home, ".local", "bin"));
            folders.Add(Path.Combine(home, ".cargo", "bin"));
        }

        if (string.Equals(name, "node", StringComparison.OrdinalIgnoreCase))
        {
            // The Windows installer, nvm-windows (NVM_SYMLINK) and a per-user install put node.exe in folders a service's PATH does not always list.
            var symlink = Environment.GetEnvironmentVariable("NVM_SYMLINK");
            if (!string.IsNullOrWhiteSpace(symlink))
            {
                folders.Add(symlink);
            }

            if (OperatingSystem.IsWindows())
            {
                folders.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs"));
                folders.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "nodejs"));
            }
        }

        if (OperatingSystem.IsMacOS())
        {
            folders.AddRange(MacFolders(name, home));
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
                if (File.Exists(candidate) && !IsXcodePlaceholder(candidate, name))
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

    /// <summary>
    /// Where macOS installers put programs, searched after PATH. A program started at sign-in by launchd gets only
    /// /usr/bin:/bin:/usr/sbin:/sbin unless the installer passed the user's own PATH, so these keep Homebrew, the .NET installer,
    /// nvm, Volta, pyenv and the python.org installer working either way.
    /// </summary>
    public static IEnumerable<string> MacFolders(string name, string? home)
    {
        var hasHome = !string.IsNullOrWhiteSpace(home);
        yield return "/opt/homebrew/bin";
        yield return "/usr/local/bin";

        if (string.Equals(name, "dotnet", StringComparison.Ordinal))
        {
            yield return "/usr/local/share/dotnet";
            if (hasHome)
            {
                yield return Path.Combine(home!, ".dotnet");
            }
        }

        if (string.Equals(name, "node", StringComparison.Ordinal))
        {
            var nvmBin = Environment.GetEnvironmentVariable("NVM_BIN");
            if (!string.IsNullOrWhiteSpace(nvmBin))
            {
                yield return nvmBin;
            }

            if (hasHome)
            {
                yield return Path.Combine(home!, ".volta", "bin");
            }
        }

        if (name is "python3" or "python")
        {
            if (hasHome)
            {
                yield return Path.Combine(home!, ".pyenv", "shims");
            }

            yield return "/Library/Frameworks/Python.framework/Versions/Current/bin";
        }
    }

    private static bool IsXcodePlaceholder(string candidate, string name) =>
        OperatingSystem.IsMacOS()
        && XcodePlaceholders.Contains(name)
        && string.Equals(Path.GetDirectoryName(candidate), "/usr/bin", StringComparison.Ordinal)
        && !DeveloperToolsInstalled.Value;

    // The places xcode-select points to after an install; checked as folders so finding git never starts a program.
    private static bool FindDeveloperTools()
    {
        var configured = Environment.GetEnvironmentVariable("DEVELOPER_DIR");
        return (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured))
            || Directory.Exists("/Library/Developer/CommandLineTools/usr/bin")
            || Directory.Exists("/Applications/Xcode.app/Contents/Developer");
    }

    private static bool IsStoreStubFolder(string folder) =>
        folder.Contains("\\WindowsApps", StringComparison.OrdinalIgnoreCase) || folder.Contains("/WindowsApps", StringComparison.OrdinalIgnoreCase);
}
