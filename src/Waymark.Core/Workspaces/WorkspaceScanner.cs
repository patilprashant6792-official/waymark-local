using System.Text.RegularExpressions;

namespace Waymark.Core.Workspaces;

/// <summary>Walks a workspace folder for solution, project and Python project files, skipping excluded and linked folders.</summary>
internal static partial class WorkspaceScanner
{
    private const int MaxFrameworkProbes = 20;
    private const long MaxProjectFileBytes = 1024 * 1024;

    // One marker per folder is kept, the first of these that the folder has.
    private static readonly string[] PythonMarkers = ["pyproject.toml", "setup.py", "Pipfile"];

    // The lock files that tell which tool manages a Python project, with the tool's name.
    private static readonly (string File, string Tool)[] PythonLocks =
    [
        ("uv.lock", "uv"),
        ("poetry.lock", "poetry"),
        ("pdm.lock", "pdm"),
        ("Pipfile.lock", "pipenv"),
        ("pylock.toml", "pylock"),
    ];

    private static readonly EnumerationOptions FileOptions = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.System,
    };

    private static readonly EnumerationOptions DirectoryOptions = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint,
    };

    public static async Task<WorkspaceMetadata> ScanAsync(string root, WorkspaceOptions options, CancellationToken ct)
    {
        var (solutions, projects, python, truncated) = await Task.Run(() => Walk(root, options, ct), ct).ConfigureAwait(false);
        var framework = await DetectFrameworkAsync(root, projects, ct).ConfigureAwait(false);
        var tool = await DetectPythonToolAsync(root, python, ct).ConfigureAwait(false);
        return new WorkspaceMetadata(solutions, projects, framework, truncated, python, tool);
    }

    private static (List<string> Solutions, List<string> Projects, List<string> Python, bool Truncated) Walk(string root, WorkspaceOptions options, CancellationToken ct)
    {
        var solutions = Directory.EnumerateFiles(root, "*.sln", FileOptions)
            .Concat(Directory.EnumerateFiles(root, "*.slnx", FileOptions))
            .Select(Path.GetFileName)
            .OfType<string>()
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var projects = new List<string>();
        var python = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        var directoriesScanned = 0;
        var truncated = false;

        while (pending.Count > 0 && !truncated)
        {
            ct.ThrowIfCancellationRequested();
            if (++directoriesScanned > options.MaxDirectoriesScanned)
            {
                truncated = true;
                break;
            }

            var directory = pending.Pop();
            string? pythonMarker = null;
            var pythonRank = int.MaxValue;
            foreach (var file in Directory.EnumerateFiles(directory, "*", FileOptions))
            {
                var name = Path.GetFileName(file);
                if (name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                {
                    if (projects.Count >= options.MaxProjectFiles)
                    {
                        truncated = true;
                        break;
                    }

                    projects.Add(Path.GetRelativePath(root, file));
                    continue;
                }

                var rank = PythonRank(name);
                if (rank < pythonRank)
                {
                    pythonRank = rank;
                    pythonMarker = file;
                }
            }

            if (pythonMarker is not null)
            {
                if (python.Count >= options.MaxProjectFiles)
                {
                    truncated = true;
                }
                else
                {
                    python.Add(Path.GetRelativePath(root, pythonMarker));
                }
            }

            foreach (var child in Directory.EnumerateDirectories(directory, "*", DirectoryOptions))
            {
                if (!WorkspacePathRules.ExcludedDirectories.Contains(Path.GetFileName(child)))
                {
                    pending.Push(child);
                }
            }
        }

        projects.Sort(StringComparer.OrdinalIgnoreCase);
        python.Sort(StringComparer.OrdinalIgnoreCase);
        return (solutions, projects, python, truncated);
    }

    /// <summary>Lower is a stronger marker of a Python project; <see cref="int.MaxValue"/> means the file is not one.</summary>
    private static int PythonRank(string fileName)
    {
        for (var i = 0; i < PythonMarkers.Length; i++)
        {
            if (string.Equals(fileName, PythonMarkers[i], StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return fileName.StartsWith("requirements", StringComparison.OrdinalIgnoreCase) && fileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
            ? PythonMarkers.Length
            : int.MaxValue;
    }

    private static async Task<string?> DetectFrameworkAsync(string root, List<string> projects, CancellationToken ct)
    {
        foreach (var project in projects.Take(MaxFrameworkProbes))
        {
            var framework = await ReadFrameworkAsync(Path.Combine(root, project), ct).ConfigureAwait(false);
            if (framework is not null)
            {
                return framework;
            }
        }

        return null;
    }

    private static async Task<string?> ReadFrameworkAsync(string projectFile, CancellationToken ct)
    {
        try
        {
            if (new FileInfo(projectFile).Length > MaxProjectFileBytes)
            {
                return null;
            }

            var content = await File.ReadAllTextAsync(projectFile, ct).ConfigureAwait(false);
            var match = TargetFrameworkRegex().Match(content);
            return match.Success
                ? match.Groups[1].Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault()
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The tool of the first Python project: its lock file first, then the tool table of its pyproject.toml, else pip.</summary>
    private static async Task<string?> DetectPythonToolAsync(string root, List<string> python, CancellationToken ct)
    {
        if (python.Count == 0)
        {
            return null;
        }

        var marker = Path.Combine(root, python[0]);
        var folders = new[] { Path.GetDirectoryName(marker) ?? root, root }.Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders)
        {
            foreach (var (file, tool) in PythonLocks)
            {
                if (File.Exists(Path.Combine(folder, file)))
                {
                    return tool;
                }
            }
        }

        if (!string.Equals(Path.GetFileName(marker), "pyproject.toml", StringComparison.OrdinalIgnoreCase))
        {
            return "pip";
        }

        try
        {
            if (new FileInfo(marker).Length > MaxProjectFileBytes)
            {
                return "pip";
            }

            var content = await File.ReadAllTextAsync(marker, ct).ConfigureAwait(false);
            if (content.Contains("[tool.poetry]", StringComparison.Ordinal))
            {
                return "poetry";
            }

            if (content.Contains("[tool.uv", StringComparison.Ordinal))
            {
                return "uv";
            }

            return content.Contains("[tool.pdm", StringComparison.Ordinal) ? "pdm" : "pip";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "pip";
        }
    }

    [GeneratedRegex(@"<TargetFrameworks?>\s*([^<]+?)\s*</TargetFrameworks?>", RegexOptions.IgnoreCase)]
    private static partial Regex TargetFrameworkRegex();
}
