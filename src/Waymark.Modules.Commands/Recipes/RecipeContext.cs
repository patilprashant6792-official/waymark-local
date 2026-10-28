using Waymark.Core.Security;
using Waymark.Core.Workspaces;

namespace Waymark.Modules.Commands.Recipes;

/// <summary>What a recipe needs: the workspace, its options, and the path guard that every path value passes through.</summary>
public sealed class RecipeContext(Workspace workspace, IPathGuard guard, RecipeOptions options)
{
    private static readonly string[] SolutionExtensions = [".sln", ".slnx"];
    private static readonly string[] ProjectExtensions = [".csproj", ".fsproj", ".vbproj"];
    private static readonly string[] BuildTargetExtensions = [".sln", ".slnx", ".csproj", ".fsproj", ".vbproj", ".proj"];

    public Workspace Workspace { get; } = workspace ?? throw new ArgumentNullException(nameof(workspace));

    public RecipeOptions Options { get; } = options ?? throw new ArgumentNullException(nameof(options));

    private IPathGuard Guard { get; } = guard ?? throw new ArgumentNullException(nameof(guard));

    public string Root => Path.GetFullPath(Workspace.Path);

    /// <summary>The absolute path of an optional folder or file option, checked by the guard. Absolute so that it can never be read as an option.</summary>
    public string? OptionalPath(string key)
    {
        var relative = Options.Text(key, 400);
        return relative is null ? null : Guard.Resolve(Workspace, relative).AbsolutePath;
    }

    public string RequiredPath(string key) => OptionalPath(key) ?? throw new ArgumentException($"Option '{key}' is required.");

    /// <summary>The absolute path of a path value that came from a list, checked by the guard.</summary>
    public string RequiredAbsolute(string relative) => Guard.Resolve(Workspace, relative).AbsolutePath;
    /// <summary>The workspace-relative path with '/' (the guard's own form) of a value, for commands that take paths after '--'.</summary>
    public string RelativePath(string relative)
    {
        var guarded = Guard.Resolve(Workspace, relative);
        return guarded.RelativePath.Length == 0 ? "." : guarded.RelativePath;
    }

    public string ExistingSolution(string key) => ExistingFile(OptionalPath(key), key, SolutionExtensions, "a .sln or .slnx file");

    public string ExistingProject(string key) => ExistingFile(OptionalPath(key), key, ProjectExtensions, "a .csproj, .fsproj or .vbproj file");

    /// <summary>The solution or project to build: the one named, else the only solution file in the workspace root, else null and dotnet decides.</summary>
    public string? BuildTarget(string key)
    {
        var named = OptionalPath(key);
        if (named is not null)
        {
            return ExistingFile(named, key, BuildTargetExtensions, "a solution or project file");
        }

        var solutions = SolutionExtensions
            .SelectMany(extension => Directory.EnumerateFiles(Root, "*" + extension, SearchOption.TopDirectoryOnly))
            .Where(file => SolutionExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
            .ToList();
        return solutions.Count == 1 ? solutions[0] : null;
    }

    /// <summary>
    /// The folder of the Python project a command works on: the option (a project folder, or the pyproject.toml, setup.py, Pipfile or requirements file in it),
    /// else the workspace folder. A command runs there, so relative imports, pytest's rootdir and the tools' own configuration are the project's.
    /// </summary>
    public string PythonProjectFolder(string key = "project")
    {
        var path = OptionalPath(key);
        if (path is null)
        {
            return Root;
        }

        if (Directory.Exists(path))
        {
            return path;
        }

        var name = Path.GetFileName(path);
        var isProjectFile = name.Equals("pyproject.toml", StringComparison.OrdinalIgnoreCase)
            || name.Equals("setup.py", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Pipfile", StringComparison.OrdinalIgnoreCase)
            || (name.StartsWith("requirements", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase));
        return isProjectFile && File.Exists(path)
            ? Path.GetDirectoryName(path) ?? Root
            : throw new ArgumentException($"Option '{key}' must be an existing project folder, or the pyproject.toml, setup.py, Pipfile or requirements file of one.");
    }

    private static string ExistingFile(string? path, string key, string[] extensions, string what)
    {
        if (path is null)
        {
            throw new ArgumentException($"Option '{key}' is required.");
        }

        if (!extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Option '{key}' must be {what}.");
        }

        return File.Exists(path) ? path : throw new ArgumentException($"Option '{key}': the file does not exist.");
    }
}
