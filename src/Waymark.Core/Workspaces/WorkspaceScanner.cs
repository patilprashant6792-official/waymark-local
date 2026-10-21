using System.Text.RegularExpressions;

namespace Waymark.Core.Workspaces;

/// <summary>Walks a workspace folder for solution and project files, skipping excluded and linked folders.</summary>
internal static partial class WorkspaceScanner
{
    private const int MaxFrameworkProbes = 20;
    private const long MaxProjectFileBytes = 1024 * 1024;

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
        var (solutions, projects, truncated) = await Task.Run(() => Walk(root, options, ct), ct).ConfigureAwait(false);
        var framework = await DetectFrameworkAsync(root, projects, ct).ConfigureAwait(false);
        return new WorkspaceMetadata(solutions, projects, framework, truncated);
    }

    private static (List<string> Solutions, List<string> Projects, bool Truncated) Walk(string root, WorkspaceOptions options, CancellationToken ct)
    {
        var solutions = Directory.EnumerateFiles(root, "*.sln", FileOptions)
            .Concat(Directory.EnumerateFiles(root, "*.slnx", FileOptions))
            .Select(Path.GetFileName)
            .OfType<string>()
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var projects = new List<string>();
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
            foreach (var file in Directory.EnumerateFiles(directory, "*.csproj", FileOptions))
            {
                if (projects.Count >= options.MaxProjectFiles)
                {
                    truncated = true;
                    break;
                }

                projects.Add(Path.GetRelativePath(root, file));
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
        return (solutions, projects, truncated);
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

    [GeneratedRegex(@"<TargetFrameworks?>\s*([^<]+?)\s*</TargetFrameworks?>", RegexOptions.IgnoreCase)]
    private static partial Regex TargetFrameworkRegex();
}
