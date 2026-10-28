using System.Globalization;
using System.Text.RegularExpressions;
using Waymark.Modules.Commands.Execution;

namespace Waymark.Modules.Commands.Recipes;

/// <summary>
/// Turns a Python action (<c>py_*</c>) and its checked options into a command plan. The same rules as every recipe: no shell, every value matched against an
/// allow-list, paths through the path guard. Python adds three of its own:
/// installs never build or run package code (wheels only, hashes checked when the file has them), the project's own interpreter
/// (a program inside the workspace) is used only for the actions that must run the project's code and is shown on the approval card,
/// and a command runs in the project folder so that imports and tool settings are the project's.
/// </summary>
public sealed class PythonRecipes(ExecutableLocator executables, ProcessRunner runner)
{
    public static readonly IReadOnlyList<string> Actions =
    [
        "py_info", "py_list", "py_venv", "py_install", "py_test", "py_lint", "py_format", "py_typecheck", "py_run", "py_package_add", "py_package_remove",
    ];

    private const string DefaultVenv = ".venv";
    private const string UvHint = "Install uv (https://docs.astral.sh/uv/), or use tool \"pip\" with a requirements file.";
    private const string RuffHint = "Install it once with 'uv tool install ruff' or 'pipx install ruff'.";
    private const int MaxFileBytes = 1_000_000;

    // Lines of a requirements file that Waymark accepts besides requirement lines: hash continuations and the index settings.
    private static readonly string[] AllowedOptionLines = ["--hash", "--index-url", "--extra-index-url", "--require-hashes"];

    private sealed record ProjectLayout(string Folder, string VenvName)
    {
        public string VenvFolder => Path.Combine(Folder, VenvName);

        public string InterpreterPath =>
            Path.Combine(VenvFolder, OperatingSystem.IsWindows() ? "Scripts" : "bin", OperatingSystem.IsWindows() ? "python.exe" : "python");

        /// <summary>The interpreter as shown in the command line, relative to the project folder the command runs in.</summary>
        public string InterpreterDisplay => $"{VenvName}/{(OperatingSystem.IsWindows() ? "Scripts/python.exe" : "bin/python")}";
    }

    public static bool Handles(string action) => Actions.Contains(action, StringComparer.Ordinal);

    public Task<CommandPlan> PlanAsync(string action, RecipeContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var plan = action switch
        {
            "py_info" => Info(context),
            "py_list" => List(context),
            "py_venv" => Venv(context),
            "py_install" => Install(context),
            "py_test" => Test(context),
            "py_lint" => Lint(context),
            "py_format" => Format(context),
            "py_typecheck" => TypeCheck(context),
            "py_run" => Run(context),
            "py_package_add" => PackageAdd(context),
            "py_package_remove" => PackageRemove(context),
            _ => throw new ArgumentException($"Unknown Python action '{action}'. Actions: {string.Join(", ", Actions)}."),
        };
        context.Options.EnsureNoUnknown();
        return Task.FromResult(plan);
    }

    private CommandPlan Info(RecipeContext c)
    {
        var layout = Layout(c);
        var project = RelativeTo(c.Root, layout.Folder);
        return new CommandPlan(
            "python.info",
            "waymark",
            [],
            TimeSpan.FromMinutes(2),
            async (write, stop) =>
            {
                var python = executables.FindPython();
                await ReportVersionAsync(write, "python", python, ["--version"], stop).ConfigureAwait(false);
                await ReportVersionAsync(write, "pip", python, ["-m", "pip", "--version"], stop).ConfigureAwait(false);
                foreach (var tool in new[] { "uv", "ruff", "pyright", "mypy" })
                {
                    await ReportVersionAsync(write, tool, executables.Find(tool), ["--version"], stop).ConfigureAwait(false);
                }

                write($"project  {project}\n");
                write($"lock     {LockFileOf(layout.Folder) ?? "none"}\n");
                write($"venv     {(File.Exists(layout.InterpreterPath) ? layout.InterpreterDisplay : "none (create it with py_venv)")}\n");
                return 0;
            },
            "check the Python tools on this computer",
            null,
            layout.Folder);
    }

    private async Task ReportVersionAsync(Action<string> write, string label, string? path, string[] arguments, CancellationToken stop)
    {
        if (path is null)
        {
            write($"{label,-8} not found\n");
            return;
        }

        var (code, output) = await runner.CaptureAsync(path, arguments, TimeSpan.FromSeconds(20), stop).ConfigureAwait(false);
        var first = output.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.Length > 0) ?? "(no output)";
        write(code == 0 ? $"{label,-8} {first}   {path}\n" : $"{label,-8} could not run: {first}\n");
    }

    private CommandPlan List(RecipeContext c)
    {
        var layout = Layout(c);
        var interpreter = RequireInterpreter(layout);
        if (ChooseTool(c, layout) == "uv")
        {
            RequireTool("uv", UvHint);
            return Make(c, "python.list", "uv", ["pip", "list", "--python", interpreter, "--format", "freeze"], layout, 2, 10);
        }

        return Make(c, "python.list", layout.InterpreterDisplay, ["-m", "pip", "list", "--format=freeze", "--disable-pip-version-check"], layout, 2, 10, interpreter);
    }

    private CommandPlan Venv(RecipeContext c)
    {
        var layout = Layout(c);
        var tool = ChooseTool(c, layout);
        var version = c.Options.Token("python", RecipeRules.PythonVersionRequest(), "a Python version such as 3.12 or 3.12.4");
        if (Directory.Exists(layout.VenvFolder))
        {
            throw new ArgumentException($"'{layout.VenvName}' already exists in {layout.Folder}. Use py_install to fill it; to start over, delete the folder yourself (a cmd_shell script the owner approves).");
        }

        if (tool == "uv")
        {
            RequireTool("uv", UvHint);
            var args = new List<string> { "venv", layout.VenvName };
            if (version is not null)
            {
                args.Add("--python");
                args.Add(version);
            }

            return Make(c, "python.venv", "uv", args, layout, 5, 30);
        }

        if (version is not null)
        {
            throw new ArgumentException("Option 'python' only works with uv, which can fetch the version. With pip the venv uses the Python found on PATH.");
        }

        var python = RequirePython();
        return Make(c, "python.venv", Path.GetFileNameWithoutExtension(python), ["-m", "venv", layout.VenvName], layout, 5, 30, python);
    }

    private CommandPlan Install(RecipeContext c)
    {
        var layout = Layout(c);
        if (ChooseTool(c, layout) == "uv")
        {
            RequireTool("uv", UvHint);
            if (!File.Exists(Path.Combine(layout.Folder, "uv.lock")))
            {
                throw new ArgumentException(
                    "uv.lock was not found in the project folder. Installing needs a lock file so every version is known and checked. " +
                    "Create it with 'uv lock --no-build' (a cmd_shell script the owner approves), or use tool \"pip\" with a requirements file.");
            }

            // The lock file decides every version; wheels only, and the project itself is not built (building it would run its build code).
            var args = new List<string> { "sync", "--frozen", "--no-build", "--no-install-project" };
            foreach (var group in c.Options.List("groups", 10, 50))
            {
                args.Add("--group");
                args.Add(CheckedName("groups", group));
            }

            foreach (var extra in c.Options.List("extras", 10, 50))
            {
                args.Add("--extra");
                args.Add(CheckedName("extras", extra));
            }

            return Make(c, "python.install", "uv", args, layout, 10, 60);
        }

        var interpreter = RequireInterpreter(layout);
        var requested = c.OptionalPath("requirements");
        var file = requested ?? Directory.EnumerateFiles(layout.Folder, "requirements.txt").FirstOrDefault()
            ?? throw new ArgumentException("There is no requirements.txt in the project folder. Name the file with option 'requirements'.");
        if (!File.Exists(file) || !file.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Option 'requirements' must be an existing requirements .txt file.");
        }

        if (new FileInfo(file).Length > MaxFileBytes)
        {
            throw new ArgumentException($"The requirements file is larger than {MaxFileBytes} bytes.");
        }

        var text = File.ReadAllText(file);
        var refused = UnsafeRequirement(text);
        if (refused is not null)
        {
            throw new ArgumentException(
                $"The requirements file is not accepted: {refused}. Waymark installs from a package index only, wheels only, so that nothing is built or run. " +
                "Remove the line, or install it yourself through cmd_shell (the owner approves the script).");
        }

        var pip = new List<string> { "-m", "pip", "install", "--only-binary", ":all:", "--no-input", "--disable-pip-version-check" };
        if (text.Contains("--hash=", StringComparison.Ordinal))
        {
            pip.Add("--require-hashes");
        }

        pip.Add("-r");
        pip.Add(ArgPath(layout.Folder, file));
        return Make(c, "python.install", layout.InterpreterDisplay, pip, layout, 10, 60, interpreter);
    }

    private CommandPlan Test(RecipeContext c)
    {
        var layout = Layout(c);
        var interpreter = RequireInterpreter(layout);
        var args = new List<string> { "-m", "pytest", "-q", "-ra", "--tb=short" };
        var filter = c.Options.Token("filter", RecipeRules.PytestExpression(), "a pytest -k expression: test names joined with and, or, not and parentheses, at most 200 characters");
        if (filter is not null)
        {
            args.Add("-k");
            args.Add(filter);
        }

        var marker = c.Options.Token("marker", RecipeRules.PytestExpression(), "a pytest -m expression: marker names joined with and, or, not and parentheses");
        if (marker is not null)
        {
            args.Add("-m");
            args.Add(marker);
        }

        if (c.Options.Number("maxFail", 1, 1000) is { } maxFail)
        {
            args.Add("--maxfail=" + maxFail.ToString(CultureInfo.InvariantCulture));
        }

        if (c.Options.Flag("verbose"))
        {
            args.Add("-v");
        }

        args.AddRange(PathArguments(c, layout, allowNodeIds: true));
        return Make(c, "python.test", layout.InterpreterDisplay, args, layout, 15, 120, interpreter);
    }

    private CommandPlan Lint(RecipeContext c)
    {
        var layout = FolderOnly(c);
        RequireTool("ruff", RuffHint);
        var args = new List<string> { "check", "--output-format", "concise", "--no-cache" };
        var paths = PathArguments(c, layout, allowNodeIds: false);
        if (paths.Count == 0)
        {
            paths.Add(".");
        }

        args.AddRange(paths);
        return Make(c, "python.lint", "ruff", args, layout, 5, 30);
    }

    private CommandPlan Format(RecipeContext c)
    {
        var layout = FolderOnly(c);
        RequireTool("ruff", RuffHint);
        var args = new List<string> { "format", "--no-cache" };
        if (c.Options.Flag("check"))
        {
            args.Add("--check");
        }

        var paths = PathArguments(c, layout, allowNodeIds: false);
        if (paths.Count == 0)
        {
            paths.Add(".");
        }

        args.AddRange(paths);
        return Make(c, "python.format", "ruff", args, layout, 5, 30);
    }

    private CommandPlan TypeCheck(RecipeContext c)
    {
        var layout = Layout(c);
        var checker = c.Options.OneOf("checker", "pyright", "mypy");
        var paths = PathArguments(c, layout, allowNodeIds: false);
        var pyright = (checker is null || checker == "pyright") ? executables.Find("pyright") : null;
        if (checker == "pyright" && pyright is null)
        {
            throw new ArgumentException("pyright was not found on this computer. Install it with 'uv tool install pyright' or 'pipx install pyright', or use checker \"mypy\".");
        }

        if (pyright is not null)
        {
            var args = new List<string>();
            if (File.Exists(layout.InterpreterPath))
            {
                args.Add("--pythonpath");
                args.Add(layout.InterpreterPath);
            }

            args.AddRange(paths);
            return Make(c, "python.typecheck", "pyright", args, layout, 10, 60);
        }

        // mypy has to see the project's packages, so it runs in the project's environment.
        var interpreter = RequireInterpreter(layout);
        var mypy = new List<string> { "-m", "mypy", "--show-column-numbers", "--no-color-output" };
        if (paths.Count == 0)
        {
            paths.Add(".");
        }

        mypy.AddRange(paths);
        return Make(c, "python.typecheck", layout.InterpreterDisplay, mypy, layout, 10, 60, interpreter);
    }

    private CommandPlan Run(RecipeContext c)
    {
        var layout = Layout(c);
        var interpreter = RequireInterpreter(layout);
        var module = c.Options.Token("module", RecipeRules.PythonModule(), "a dotted module name such as uvicorn or app.main");
        var script = c.OptionalPath("script");
        if ((module is null) == (script is null))
        {
            throw new ArgumentException("Give exactly one of 'module' (run with python -m) or 'script' (a .py file in the workspace).");
        }

        var args = new List<string>();
        if (module is not null)
        {
            args.Add("-m");
            args.Add(module);
        }
        else
        {
            var scriptFile = script ?? throw new ArgumentException("Option 'script' is required.");
            if (!File.Exists(scriptFile) || !scriptFile.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Option 'script' must be an existing .py file.");
            }

            args.Add(ArgPath(layout.Folder, scriptFile));
        }

        args.AddRange(c.Options.List("appArgs", 30, 300));
        return Make(c, "python.run", layout.InterpreterDisplay, args, layout, 30, 480, interpreter);
    }

    private CommandPlan PackageAdd(RecipeContext c)
    {
        var layout = FolderOnly(c);
        RequireUv(c, layout, "py_package_add");
        var package = c.Options.RequiredToken("package", RecipeRules.PackageId(), "a package name (letters, digits, '.', '_' and '-')");
        var version = c.Options.RequiredToken("version", RecipeRules.PythonVersion(), "an exact version such as 2.32.3, as pkg_find shows it");
        var args = new List<string> { "add", "--no-build", $"{package}=={version}" };
        var group = c.Options.Token("group", RecipeRules.PythonGroup(), "a dependency group name such as dev");
        if (group is not null)
        {
            args.Add("--group");
            args.Add(group);
        }

        return Make(c, "python.package_add", "uv", args, layout, 10, 30);
    }

    private CommandPlan PackageRemove(RecipeContext c)
    {
        var layout = FolderOnly(c);
        RequireUv(c, layout, "py_package_remove");
        var package = c.Options.RequiredToken("package", RecipeRules.PackageId(), "a package name (letters, digits, '.', '_' and '-')");
        var args = new List<string> { "remove", "--no-build", package };
        var group = c.Options.Token("group", RecipeRules.PythonGroup(), "a dependency group name such as dev");
        if (group is not null)
        {
            args.Add("--group");
            args.Add(group);
        }

        return Make(c, "python.package_remove", "uv", args, layout, 10, 30);
    }

    /// <summary>Adding and removing a package edits pyproject.toml and the lock file, which only uv does here; pip projects keep their requirements file by hand.</summary>
    private void RequireUv(RecipeContext c, ProjectLayout layout, string action)
    {
        var tool = ChooseTool(c, layout);
        if (tool != "uv")
        {
            throw new ArgumentException(
                $"{action} edits pyproject.toml and uv.lock, so it needs uv. This project uses pip: change its requirements file with file_edit_lines (name==version, with --hash lines if the file has them), then run py_install.");
        }

        RequireTool("uv", UvHint);
    }

    private static ProjectLayout Layout(RecipeContext c)
    {
        var folder = c.PythonProjectFolder();
        var venv = c.Options.Token("venv", RecipeRules.FolderName(), "a folder name such as .venv") ?? DefaultVenv;
        return new ProjectLayout(folder, venv);
    }

    private static ProjectLayout FolderOnly(RecipeContext c) => new(c.PythonProjectFolder(), DefaultVenv);

    /// <summary>uv when the project has a uv.lock, pip when it has a requirements file, uv otherwise; the option 'tool' overrides.</summary>
    private static string ChooseTool(RecipeContext c, ProjectLayout layout)
    {
        var requested = c.Options.OneOf("tool", "uv", "pip");
        if (requested is not null)
        {
            return requested;
        }

        if (File.Exists(Path.Combine(layout.Folder, "uv.lock")))
        {
            return "uv";
        }

        if (File.Exists(Path.Combine(layout.Folder, "poetry.lock")) || FileContains(Path.Combine(layout.Folder, "pyproject.toml"), "[tool.poetry]"))
        {
            throw new ArgumentException(
                "This project is managed by Poetry, which these actions do not run yet. Use tool \"pip\" with a requirements file, or run poetry through cmd_shell (the owner approves the script).");
        }

        return Directory.EnumerateFiles(layout.Folder, "requirements*.txt").Any() ? "pip" : "uv";
    }

    private static string RequireInterpreter(ProjectLayout layout) =>
        File.Exists(layout.InterpreterPath)
            ? layout.InterpreterPath
            : throw new ArgumentException($"There is no virtual environment '{layout.VenvName}' in {layout.Folder}. Create it with py_venv, then fill it with py_install.");

    private string RequireTool(string name, string hint) =>
        executables.Find(name) ?? throw new ArgumentException($"'{name}' was not found on this computer. {hint}");

    private string RequirePython() =>
        executables.FindPython() ?? throw new ArgumentException("Python was not found in the folders of PATH. Install Python from python.org (with the option that adds it to PATH), or install uv, and try again.");

    private static CommandPlan Make(
        RecipeContext c,
        string operation,
        string program,
        IReadOnlyList<string> args,
        ProjectLayout layout,
        int defaultMinutes,
        int maxMinutes,
        string? executable = null)
    {
        var minutes = c.Options.Number("timeoutMinutes", 1, maxMinutes) ?? defaultMinutes;
        return new CommandPlan(operation, program, args, TimeSpan.FromMinutes(minutes), null, null, executable, layout.Folder);
    }

    /// <summary>The 'paths' option: workspace-relative files or folders (for pytest also node ids such as tests/test_a.py::TestA::test_b), as arguments relative to the project folder.</summary>
    private static List<string> PathArguments(RecipeContext c, ProjectLayout layout, bool allowNodeIds)
    {
        var result = new List<string>();
        foreach (var entry in c.Options.List("paths", 20, 300))
        {
            var file = entry;
            string? node = null;
            var split = allowNodeIds ? entry.IndexOf("::", StringComparison.Ordinal) : -1;
            if (split > 0)
            {
                file = entry[..split];
                node = entry[(split + 2)..];
            }

            var absolute = c.RequiredAbsolute(file);
            if (!File.Exists(absolute) && !Directory.Exists(absolute))
            {
                throw new ArgumentException($"Option 'paths': '{file}' does not exist.");
            }

            var argument = ArgPath(layout.Folder, absolute);
            if (node is not null)
            {
                if (!Matches(RecipeRules.PytestNodeId(), node))
                {
                    throw new ArgumentException($"Option 'paths': '{node}' is not a test name (letters, digits and _ [ ] - . , = + : and space, starting with a letter or _).");
                }

                argument += "::" + node;
            }

            result.Add(argument);
        }

        return result;
    }

    /// <summary>A path as an argument: relative to the project folder and never starting with '-', so it cannot be read as an option.</summary>
    private static string ArgPath(string folder, string absolute)
    {
        var relative = Path.GetRelativePath(folder, absolute).Replace('\\', '/');
        if (relative == ".")
        {
            return ".";
        }

        return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? absolute : "./" + relative;
    }

    private static string RelativeTo(string root, string folder)
    {
        var relative = Path.GetRelativePath(root, folder).Replace('\\', '/');
        return relative == "." ? "the workspace folder" : relative;
    }

    private static string CheckedName(string option, string value) =>
        Matches(RecipeRules.PythonGroup(), value)
            ? value
            : throw new ArgumentException($"Option '{option}' has an entry that is not a name: letters, digits, '.', '_' and '-', starting with a letter.");

    private static bool Matches(Regex pattern, string text)
    {
        try
        {
            return pattern.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static string? LockFileOf(string folder) =>
        new[] { "uv.lock", "poetry.lock", "pdm.lock", "pylock.toml" }.FirstOrDefault(name => File.Exists(Path.Combine(folder, name)));

    private static bool FileContains(string path, string text)
    {
        try
        {
            return File.Exists(path) && new FileInfo(path).Length <= MaxFileBytes && File.ReadAllText(path).Contains(text, StringComparison.Ordinal);
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// The first line of a requirements file that Waymark will not install from, with its number, or null. Accepted: requirement lines (name, specifier, markers),
    /// comments, hash continuations and index settings. Refused: editable and path or URL installs and any other option (-r, -c, -f, --no-binary ...), because those
    /// build code or pull in files that were not checked.
    /// </summary>
    internal static string? UnsafeRequirement(string text)
    {
        var number = 0;
        foreach (var raw in text.Split('\n'))
        {
            number++;
            var line = raw.Trim().TrimEnd('\\').Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var shown = line.Length > 60 ? line[..60] + "..." : line;
            if (line[0] == '-')
            {
                if (!AllowedOptionLines.Any(allowed => line.StartsWith(allowed, StringComparison.Ordinal)))
                {
                    return string.Create(CultureInfo.InvariantCulture, $"line {number} '{shown}' is an option other than --hash or an index setting");
                }

                continue;
            }

            if (line[0] is '.' or '/' or '~' || line.Contains("://", StringComparison.Ordinal) || line.Contains(" @ ", StringComparison.Ordinal) || line.StartsWith("git+", StringComparison.Ordinal))
            {
                return string.Create(CultureInfo.InvariantCulture, $"line {number} '{shown}' installs from a path or an address, which builds or fetches code");
            }
        }

        return null;
    }
}
