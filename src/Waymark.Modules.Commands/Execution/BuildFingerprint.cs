using System.Security.Cryptography;
using System.Text.Json;

namespace Waymark.Modules.Commands.Execution;

/// <summary>
/// A hash of every file that decides what a build, a package restore or a git command will run: project and solution files, props and targets,
/// tool and SDK settings, git config and hooks. Those files are code in all but name, so a changed one means the next command might do
/// something the user has not seen; the policy then asks again.
/// </summary>
public static class BuildFingerprint
{
    private const int MaxFiles = 5000;
    private const long MaxFileBytes = 4 * 1024 * 1024;

    private static readonly HashSet<string> NodeScriptExtensions = new(StringComparer.OrdinalIgnoreCase) { ".js", ".cjs", ".mjs", ".ts", ".cts", ".mts" };

    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", ".git", ".vs", ".idea", "packages", ".vscode", "TestResults",
        ".venv", "venv", "__pycache__", "site-packages", ".tox", ".nox", ".mypy_cache", ".pytest_cache", ".ruff_cache",
        ".next", ".nuxt", ".svelte-kit", ".turbo", ".parcel-cache", ".angular", ".nx", "dist", "coverage",
    };

    private static readonly HashSet<string> BuildExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".csproj", ".vbproj", ".fsproj", ".proj", ".sln", ".slnx", ".props", ".targets",
    };

    // conftest.py and noxfile.py run when pytest or nox starts; the tool files decide which checker and which Python a command uses.
    private static readonly HashSet<string> BuildFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "global.json", "nuget.config", "dotnet-tools.json", ".gitattributes",
        "pyproject.toml", "setup.py", "setup.cfg", "uv.lock", "uv.toml", "poetry.lock", "pdm.lock", "pylock.toml", "Pipfile", "Pipfile.lock",
        "conftest.py", "noxfile.py", "tox.ini", "pytest.ini", "ruff.toml", ".ruff.toml", "mypy.ini", "pyrightconfig.json", ".python-version",
        "package.json", "package-lock.json", "npm-shrinkwrap.json", "pnpm-lock.yaml", "pnpm-workspace.yaml", "yarn.lock", ".npmrc", ".yarnrc.yml", ".nvmrc", ".node-version",
    };

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public static async Task<IReadOnlyDictionary<string, string>> ComputeAsync(string workspaceRoot, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        var root = Path.GetFullPath(workspaceRoot);
        var hashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var folder = pending.Pop();
            foreach (var entry in SafeEntries(folder))
            {
                if (hashes.Count >= MaxFiles)
                {
                    return hashes;
                }

                var attributes = SafeAttributes(entry);
                if (attributes is null || attributes.Value.HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }

                if (attributes.Value.HasFlag(FileAttributes.Directory))
                {
                    if (!SkippedFolders.Contains(Path.GetFileName(entry)))
                    {
                        pending.Push(entry);
                    }

                    continue;
                }

                if (IsBuildFile(entry))
                {
                    await AddAsync(hashes, root, entry, ct).ConfigureAwait(false);
                }
            }
        }

        await AddGitAsync(hashes, root, ct).ConfigureAwait(false);
        return hashes;
    }

    public static string Serialize(IReadOnlyDictionary<string, string> fingerprint) => JsonSerializer.Serialize(fingerprint, Json);

    public static IReadOnlyDictionary<string, string> Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json, Json) ?? new Dictionary<string, string>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>();
        }
    }

    /// <summary>The files that were added, removed or changed, sorted by name.</summary>
    public static IReadOnlyList<string> Differences(IReadOnlyDictionary<string, string> trusted, IReadOnlyDictionary<string, string> current)
    {
        var changed = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (path, hash) in current)
        {
            if (!trusted.TryGetValue(path, out var known))
            {
                changed.Add(path + " (new)");
            }
            else if (!string.Equals(known, hash, StringComparison.Ordinal))
            {
                changed.Add(path + " (changed)");
            }
        }

        foreach (var path in trusted.Keys)
        {
            if (!current.ContainsKey(path))
            {
                changed.Add(path + " (removed)");
            }
        }

        return changed.ToList();
    }

    private static bool IsBuildFile(string path)
    {
        var name = Path.GetFileName(path);
        return BuildFileNames.Contains(name)
            || BuildExtensions.Contains(Path.GetExtension(name))
            || (name.StartsWith("requirements", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            || (name.StartsWith("tsconfig", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            || (name.Contains(".config.", StringComparison.OrdinalIgnoreCase) && NodeScriptExtensions.Contains(Path.GetExtension(name)))
            || name.StartsWith(".eslintrc", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith(".babelrc", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task AddGitAsync(SortedDictionary<string, string> hashes, string root, CancellationToken ct)
    {
        var git = Path.Combine(root, ".git");
        if (!Directory.Exists(git))
        {
            return;
        }

        var config = Path.Combine(git, "config");
        if (File.Exists(config))
        {
            await AddAsync(hashes, root, config, ct).ConfigureAwait(false);
        }

        var hooks = Path.Combine(git, "hooks");
        if (!Directory.Exists(hooks))
        {
            return;
        }

        foreach (var hook in SafeEntries(hooks))
        {
            if (!hook.EndsWith(".sample", StringComparison.OrdinalIgnoreCase) && File.Exists(hook))
            {
                await AddAsync(hashes, root, hook, ct).ConfigureAwait(false);
            }
        }
    }

    private static async Task AddAsync(SortedDictionary<string, string> hashes, string root, string file, CancellationToken ct)
    {
        var key = Path.GetRelativePath(root, file).Replace('\\', '/');
        try
        {
            var info = new FileInfo(file);
            if (info.Length > MaxFileBytes)
            {
                hashes[key] = "size:" + info.Length;
                return;
            }

            await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
            hashes[key] = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
        }
        catch (IOException)
        {
            hashes[key] = "unreadable";
        }
        catch (UnauthorizedAccessException)
        {
            hashes[key] = "unreadable";
        }
    }

    private static IEnumerable<string> SafeEntries(string folder)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(folder).ToList();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static FileAttributes? SafeAttributes(string path)
    {
        try
        {
            return File.GetAttributes(path);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
