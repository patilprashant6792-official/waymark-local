using System.Globalization;
using System.Text.RegularExpressions;
using Waymark.Core.Security;
using Waymark.Core.Text;
using Waymark.Core.Tools;

namespace Waymark.Modules.Code.Skeleton;

/// <summary>
/// Renders the Python projects under the viewed folder, the way <see cref="ProjectFileSummarizer"/> renders the .NET projects: the declared
/// dependencies with the version the lock file pins (uv.lock, poetry.lock, pdm.lock, pylock.toml), so an agent knows which version to look up
/// before it calls a package. One project per folder: its pyproject.toml, else setup.py, else Pipfile, else a requirements file. setup.py is code and
/// is not read.
/// </summary>
internal sealed partial class PythonProjectSummarizer(FileAccessPolicy policy, SkeletonOptions options)
{
    private const int MaxFileChars = 2_000_000;
    private const string NotLocked = "not locked";

    private static readonly FileStreamOptions ReadOptions = new()
    {
        Mode = FileMode.Open,
        Access = FileAccess.Read,
        Share = FileShare.ReadWrite | FileShare.Delete,
    };

    private sealed record Dependency(string Name, string Spec, string? Group);

    private sealed record PythonFacts(
        string RelativePath,
        string? Name,
        string? RequiresPython,
        string Tool,
        string? LockFile,
        IReadOnlyDictionary<string, string> Locked,
        IReadOnlyList<Dependency> Dependencies,
        string? Note,
        string? Error);

    /// <summary>Markdown for the Python projects below the folder; empty when there are none.</summary>
    public async Task<string> RenderAsync(
        string root,
        string scopeAbsolute,
        string scopeRelative,
        string workspaceName,
        WorkspaceAccessRules rules,
        bool overview,
        int maxChars,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var (markers, truncated) = VisibleEntries.Scan(root, scopeAbsolute, policy, rules, file => Rank(file.Name) < int.MaxValue, options.MaxScanEntries, options.MaxListedFiles, ct);
        var chosen = markers
            .GroupBy(m => m.Info.DirectoryName ?? root, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(m => Rank(m.Info.Name)).ThenBy(m => m.Info.Name, StringComparer.OrdinalIgnoreCase).First())
            .OrderBy(m => m.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (chosen.Count == 0)
        {
            return string.Empty;
        }

        var facts = new List<PythonFacts>(chosen.Count);
        foreach (var marker in chosen)
        {
            ct.ThrowIfCancellationRequested();
            facts.Add(await ParseAsync(root, marker, ct).ConfigureAwait(false));
        }

        var output = new OutputBudget(maxChars);
        if (!overview || facts.Count <= options.MaxProjectsInline)
        {
            foreach (var project in facts)
            {
                AppendProject(project, output);
            }
        }
        else
        {
            AppendSummary(facts, scopeRelative, workspaceName, output);
        }

        if (truncated)
        {
            output.TryAppendLine($"_More Python project files exist than are listed (limit {options.MaxListedFiles}, or the workspace is very large)._");
        }

        return output.Build().TrimEnd();
    }

    /// <summary>Lower is a stronger marker of a Python project; <see cref="int.MaxValue"/> means the file is not one.</summary>
    private static int Rank(string fileName)
    {
        if (fileName.Equals("pyproject.toml", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (fileName.Equals("setup.py", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (fileName.Equals("Pipfile", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        return fileName.StartsWith("requirements", StringComparison.OrdinalIgnoreCase) && fileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
            ? 3
            : int.MaxValue;
    }

    private async Task<PythonFacts> ParseAsync(string root, VisibleFile marker, CancellationToken ct)
    {
        var folder = marker.Info.DirectoryName ?? root;
        try
        {
            var pins = await PythonLockFiles.ReadAsync(root, folder, ct).ConfigureAwait(false);
            var lockFile = pins?.LockFile;
            IReadOnlyDictionary<string, string> locked = pins?.Versions ?? new Dictionary<string, string>();
            var name = marker.Info.Name;
            if (name.Equals("pyproject.toml", StringComparison.OrdinalIgnoreCase))
            {
                return await ParsePyprojectAsync(marker, lockFile, locked, ct).ConfigureAwait(false);
            }

            if (name.StartsWith("requirements", StringComparison.OrdinalIgnoreCase))
            {
                var text = await ReadAsync(marker.Info.FullName, ct).ConfigureAwait(false);
                return new PythonFacts(marker.RelativePath, null, null, "pip", lockFile, locked, ParseRequirements(text), null, null);
            }

            var note = name.Equals("setup.py", StringComparison.OrdinalIgnoreCase)
                ? "Dependencies are declared in setup.py, which is code and is not read. Look at install_requires with code_read."
                : "Dependencies are declared in the Pipfile. Read it with code_read; Pipfile.lock is not summarised.";
            return new PythonFacts(marker.RelativePath, null, null, name.Equals("Pipfile", StringComparison.OrdinalIgnoreCase) ? "pipenv" : "setuptools", lockFile, locked, [], note, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new PythonFacts(marker.RelativePath, null, null, "pip", null, new Dictionary<string, string>(), [], null, "The project file cannot be read");
        }
    }

    private async Task<PythonFacts> ParsePyprojectAsync(VisibleFile marker, string? lockFile, IReadOnlyDictionary<string, string> locked, CancellationToken ct)
    {
        var text = await ReadAsync(marker.Info.FullName, ct).ConfigureAwait(false);
        string? name = null;
        string? requiresPython = null;
        var tool = lockFile is null ? null : PythonLockFiles.ToolOf(lockFile);
        var dependencies = new List<Dependency>();
        foreach (var section in TomlLite.Parse(text).Where(s => !s.IsArrayItem))
        {
            var table = section.Name;
            if (table == "project")
            {
                name = TomlLite.AsString(section.Values.GetValueOrDefault("name")) ?? name;
                requiresPython = TomlLite.AsString(section.Values.GetValueOrDefault("requires-python"));
                dependencies.AddRange(TomlLite.Strings(section.Values.GetValueOrDefault("dependencies")).Select(spec => FromRequirement(spec, null)));
            }
            else if (table is "project.optional-dependencies" or "dependency-groups")
            {
                foreach (var (group, raw) in section.Values)
                {
                    dependencies.AddRange(TomlLite.Strings(raw).Select(spec => FromRequirement(spec, group)));
                }
            }
            else if (table == "tool.poetry")
            {
                name ??= TomlLite.AsString(section.Values.GetValueOrDefault("name"));
                tool ??= "poetry";
            }
            else if (table == "tool.poetry.dependencies" || (table.StartsWith("tool.poetry.group.", StringComparison.Ordinal) && table.EndsWith(".dependencies", StringComparison.Ordinal)))
            {
                var group = table == "tool.poetry.dependencies"
                    ? null
                    : table["tool.poetry.group.".Length..^".dependencies".Length];
                foreach (var (package, raw) in section.Values)
                {
                    if (package.Equals("python", StringComparison.OrdinalIgnoreCase))
                    {
                        requiresPython ??= PoetrySpec(raw);
                        continue;
                    }

                    dependencies.Add(new Dependency(package, PoetrySpec(raw), group));
                }
            }
            else if (table.StartsWith("tool.uv", StringComparison.Ordinal))
            {
                tool ??= "uv";
            }
            else if (table.StartsWith("tool.pdm", StringComparison.Ordinal))
            {
                tool ??= "pdm";
            }
        }

        return new PythonFacts(marker.RelativePath, name, requiresPython, tool ?? "pip", lockFile, locked, dependencies, null, null);
    }

    private static Dependency FromRequirement(string requirement, string? group)
    {
        var match = RequirementPattern().Match(requirement);
        if (!match.Success)
        {
            return new Dependency(requirement.Trim(), string.Empty, group);
        }

        var rest = match.Groups["rest"].Value;
        var marker = rest.IndexOf(';', StringComparison.Ordinal);
        var spec = (marker < 0 ? rest : rest[..marker]).Trim().Replace(" ", string.Empty, StringComparison.Ordinal);
        return new Dependency(match.Groups["name"].Value, spec, group);
    }

    /// <summary>A poetry dependency is a version string, or a table with a version key.</summary>
    private static string PoetrySpec(string raw)
    {
        var text = TomlLite.AsString(raw);
        if (text is not null)
        {
            return text;
        }

        var match = PoetryVersionPattern().Match(raw);
        return match.Success ? match.Groups["version"].Value : string.Empty;
    }

    private static List<Dependency> ParseRequirements(string text)
    {
        var dependencies = new List<Dependency>();
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim().TrimEnd('\\').Trim();
            var comment = line.IndexOf(" #", StringComparison.Ordinal);
            if (comment >= 0)
            {
                line = line[..comment].Trim();
            }

            if (line.Length == 0 || line[0] == '#' || line[0] == '-')
            {
                continue;
            }

            var match = RequirementPattern().Match(line);
            if (!match.Success)
            {
                continue;
            }

            var rest = match.Groups["rest"].Value;
            var marker = rest.IndexOf(';', StringComparison.Ordinal);
            var spec = (marker < 0 ? rest : rest[..marker]).Trim().Replace(" ", string.Empty, StringComparison.Ordinal);
            dependencies.Add(new Dependency(match.Groups["name"].Value, spec, null));
        }

        return dependencies;
    }

    private static async Task<string> ReadAsync(string path, CancellationToken ct)
    {
        using var stream = new FileStream(path, ReadOptions);
        using var reader = new StreamReader(stream);
        var buffer = new char[MaxFileChars + 1];
        var read = await reader.ReadBlockAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
        return new string(buffer, 0, Math.Min(read, MaxFileChars));
    }

    private static void AppendProject(PythonFacts project, OutputBudget output)
    {
        output.TryAppendLine($"## Python Dependencies: {project.RelativePath}");
        output.TryAppendLine();
        if (project.Error is { } error)
        {
            output.TryAppendLine($"*[{error}]*");
            output.TryAppendLine();
            return;
        }

        if (project.Note is { } note)
        {
            output.TryAppendLine($"*{note}*");
            output.TryAppendLine();
        }

        var direct = project.Dependencies.Where(d => d.Group is null).ToList();
        var grouped = project.Dependencies.Where(d => d.Group is not null).ToList();
        if (project.Dependencies.Count == 0)
        {
            output.TryAppendLine("*No dependencies read*");
        }

        if (direct.Count > 0)
        {
            output.TryAppendLine($"**Packages ({direct.Count}):**");
            foreach (var dependency in direct)
            {
                output.TryAppendLine($"• {Describe(dependency, project.Locked)}");
            }
        }

        if (grouped.Count > 0)
        {
            output.TryAppendLine($"**Optional and development packages ({grouped.Count}):**");
            foreach (var dependency in grouped)
            {
                output.TryAppendLine($"• [{dependency.Group}] {Describe(dependency, project.Locked)}");
            }
        }

        output.TryAppendLine();
        if (project.Name is not null)
        {
            output.TryAppendLine($"**Name:** {project.Name}");
        }

        output.TryAppendLine($"**Tool:** {project.Tool}{(project.LockFile is null ? " (no lock file found)" : $" (lock: {project.LockFile}, {project.Locked.Count.ToString(CultureInfo.InvariantCulture)} packages locked)")}");
        output.TryAppendLine($"**Requires Python:** {project.RequiresPython ?? "Unknown"}");
        output.TryAppendLine();
    }

    private static void AppendSummary(List<PythonFacts> projects, string scopeRelative, string workspaceName, OutputBudget output)
    {
        var scope = scopeRelative.Length == 0 ? "the workspace" : $"`{scopeRelative}`";
        output.TryAppendLine(string.Create(CultureInfo.InvariantCulture, $"## Python Dependencies: {projects.Count} projects in {scope}"));
        output.TryAppendLine();
        foreach (var project in projects)
        {
            var locked = project.LockFile is null ? "no lock" : "locked";
            output.TryAppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"• `{project.RelativePath}` - {project.Tool}, {locked}, {project.Dependencies.Count} dependencies, Python {project.RequiresPython ?? "unknown"}"));
        }

        output.TryAppendLine();
        output.TryAppendLine($"_One project's own pins: code_skeleton(\"{workspaceName}\", path=\"<project folder>\")._");
        output.TryAppendLine();
    }

    /// <summary>The exact locked version, with the declared range when it says more; else the declared range and a note that nothing pins it.</summary>
    private static string Describe(Dependency dependency, IReadOnlyDictionary<string, string> locked)
    {
        if (locked.TryGetValue(PythonLockFiles.Normalize(dependency.Name), out var version))
        {
            var spec = dependency.Spec;
            var same = spec.Length == 0 || spec == "==" + version;
            return same ? $"{dependency.Name} `{version}`" : $"{dependency.Name} `{version}` (declared {spec})";
        }

        return dependency.Spec.Length == 0
            ? $"{dependency.Name} `unpinned` ({NotLocked})"
            : $"{dependency.Name} `{dependency.Spec}` ({NotLocked})";
    }

    [GeneratedRegex(@"^\s*(?<name>[A-Za-z0-9][A-Za-z0-9._-]*)\s*(\[[^\]]*\])?\s*(?<rest>.*)$", RegexOptions.None, 200)]
    private static partial Regex RequirementPattern();

    [GeneratedRegex(@"version\s*=\s*[""'](?<version>[^""']*)[""']", RegexOptions.None, 200)]
    private static partial Regex PoetryVersionPattern();
}
