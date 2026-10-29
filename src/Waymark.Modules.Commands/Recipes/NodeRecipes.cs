using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Waymark.Modules.Commands.Execution;

namespace Waymark.Modules.Commands.Recipes;

/// <summary>
/// Turns a Node action (<c>node_*</c>) and its checked options into a command plan. The same rules as every recipe: no shell, every value matched against an
/// allow-list, paths through the path guard. Node adds its own:
/// npm is started as <c>node npm-cli.js</c>, never as the <c>npm.cmd</c> shim (a .cmd file is read by cmd.exe, which would interpret the arguments),
/// installs run no lifecycle scripts (<c>--ignore-scripts</c>, from the lock file only), and a script runs only when it is named in the package.json that is read here.
/// Only npm installs and edits packages in this version; a pnpm, yarn or bun project can still build, test and run its scripts.
/// </summary>
public sealed class NodeRecipes(ExecutableLocator executables, ProcessRunner runner)
{
    public static readonly IReadOnlyList<string> Actions =
    [
        "node_info", "node_list", "node_install", "node_typecheck", "node_build", "node_test", "node_lint", "node_script", "node_run", "node_package_add", "node_package_remove",
    ];

    private const int MaxManifestBytes = 2_000_000;
    private const int MaxScriptPreviewChars = 160;
    private const string NodeHint = "Install Node.js (https://nodejs.org) and try again.";

    private static readonly JsonDocumentOptions ManifestOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private static readonly string[] OtherManagerLocks = ["pnpm-lock.yaml", "yarn.lock", "bun.lockb", "bun.lock"];

    private sealed record Project(string Folder, string Manifest, IReadOnlyDictionary<string, string> Scripts, string? PackageManagerField);

    public static bool Handles(string action) => Actions.Contains(action, StringComparer.Ordinal);

    public Task<CommandPlan> PlanAsync(string action, RecipeContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var plan = action switch
        {
            "node_info" => Info(context),
            "node_list" => List(context),
            "node_install" => Install(context),
            "node_typecheck" => TypeCheck(context),
            "node_build" => RunScript(context, "node.build", "build", 30, 120),
            "node_test" => RunScript(context, "node.test", "test", 15, 120),
            "node_lint" => RunScript(context, "node.lint", "lint", 10, 60),
            "node_script" => RunScript(context, "node.script", null, 15, 120),
            "node_run" => RunLong(context),
            "node_package_add" => PackageAdd(context),
            "node_package_remove" => PackageRemove(context),
            _ => throw new ArgumentException($"Unknown Node action '{action}'. Actions: {string.Join(", ", Actions)}."),
        };
        context.Options.EnsureNoUnknown();
        return Task.FromResult(plan);
    }

    private CommandPlan Info(RecipeContext c)
    {
        var project = ReadProject(c);
        var relative = Path.GetRelativePath(c.Root, project.Folder).Replace('\\', '/');
        var shown = relative == "." ? "the workspace folder" : relative;
        return new CommandPlan(
            "node.info",
            "waymark",
            [],
            TimeSpan.FromMinutes(2),
            async (write, stop) =>
            {
                var node = executables.Find("node");
                if (node is null)
                {
                    write($"node     not found. {NodeHint}\n");
                }
                else
                {
                    await ReportVersionAsync(write, "node", node, ["--version"], stop).ConfigureAwait(false);
                    var cli = executables.FindNpmCli(node);
                    if (cli is null)
                    {
                        write("npm      not found beside node\n");
                    }
                    else
                    {
                        await ReportVersionAsync(write, "npm", node, [cli, "--version"], stop).ConfigureAwait(false);
                    }
                }

                write($"project  {shown}\n");
                write($"manager  {ManagerOf(project)}{(File.Exists(Path.Combine(project.Folder, "package-lock.json")) ? " (package-lock.json)" : string.Empty)}\n");
                write($"modules  {(Directory.Exists(Path.Combine(project.Folder, "node_modules")) ? "node_modules is present" : "no node_modules (run node_install)")}\n");
                write($"tsc      {TypeScriptVersion(project) ?? "not installed in the project"}\n");
                write($"scripts  {(project.Scripts.Count == 0 ? "none" : string.Join(", ", project.Scripts.Keys.Take(20)))}\n");
                return 0;
            },
            "check the Node tools on this computer and in the project",
            null,
            project.Folder);
    }

    private async Task ReportVersionAsync(Action<string> write, string label, string path, string[] arguments, CancellationToken stop)
    {
        var (code, output) = await runner.CaptureAsync(path, arguments, TimeSpan.FromSeconds(20), stop).ConfigureAwait(false);
        var first = output.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.Length > 0) ?? "(no output)";
        write(code == 0 ? $"{label,-8} {first}\n" : $"{label,-8} could not run: {first}\n");
    }

    private CommandPlan List(RecipeContext c)
    {
        var project = ReadProject(c);
        RequireModules(project);
        return Npm(c, "node.list", "npm ls --depth=0", ["ls", "--depth=0", "--no-fund", "--no-audit"], project, 2, 10);
    }

    private CommandPlan Install(RecipeContext c)
    {
        var project = ReadProject(c);
        RequireNpm(project, "node_install");
        if (!File.Exists(Path.Combine(project.Folder, "package-lock.json")))
        {
            throw new ArgumentException(
                "package-lock.json was not found in the project folder. Installing needs a lock file so every version is known and checked. " +
                "Create it with 'npm install --package-lock-only --ignore-scripts' (a cmd_shell script the owner approves).");
        }

        return Npm(c, "node.install", "npm ci --ignore-scripts", ["ci", "--ignore-scripts", "--no-audit", "--no-fund"], project, 10, 60);
    }

    private CommandPlan TypeCheck(RecipeContext c)
    {
        var project = ReadProject(c);
        var tsc = Path.Combine(project.Folder, "node_modules", "typescript", "bin", "tsc");
        if (!File.Exists(tsc))
        {
            throw new ArgumentException("typescript is not installed in this project (no node_modules/typescript). Install the dependencies with node_install first.");
        }

        var args = new List<string> { tsc, "--noEmit", "--pretty", "false" };
        var display = "tsc --noEmit";
        var tsconfig = c.OptionalPath("tsconfig");
        if (tsconfig is not null)
        {
            if (!File.Exists(tsconfig) || !tsconfig.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Option 'tsconfig' must be an existing .json file.");
            }

            var relative = Path.GetRelativePath(project.Folder, tsconfig).Replace('\\', '/');
            args.Add("-p");
            args.Add(relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? tsconfig : "./" + relative);
            display += " -p " + relative;
        }

        return Node(c, "node.typecheck", display, args, project, 10, 60);
    }

    /// <summary>A named script of package.json run to the end: build, test, lint or any other (node_script). Extra arguments follow a -- so the script's own program reads them.</summary>
    private CommandPlan RunScript(RecipeContext c, string operation, string? defaultScript, int defaultMinutes, int maxMinutes)
    {
        var project = ReadProject(c);
        var name = ScriptName(c, project, defaultScript);
        var args = new List<string> { "run", name };
        var extra = c.Options.List("args", 20, 200);
        if (extra.Count > 0)
        {
            args.Add("--");
            foreach (var item in extra)
            {
                args.Add(CheckedArgument(item));
            }
        }

        return Npm(c, operation, Display(project, name, extra), args, project, defaultMinutes, maxMinutes);
    }

    private CommandPlan RunLong(RecipeContext c)
    {
        var project = ReadProject(c);
        var requested = c.Options.Token("script", RecipeRules.NpmScriptName(), "a script name of package.json");
        var name = requested ?? (project.Scripts.ContainsKey("dev") ? "dev" : project.Scripts.ContainsKey("start") ? "start" : null)
            ?? throw new ArgumentException("Option 'script' is required: package.json has neither a 'dev' nor a 'start' script.");
        RequireScript(project, name);
        var args = new List<string> { "run", name };
        var extra = c.Options.List("args", 20, 200);
        if (extra.Count > 0)
        {
            args.Add("--");
            foreach (var item in extra)
            {
                args.Add(CheckedArgument(item));
            }
        }

        return Npm(c, "node.run", Display(project, name, extra), args, project, 30, 480);
    }

    private CommandPlan PackageAdd(RecipeContext c)
    {
        var project = ReadProject(c);
        RequireNpm(project, "node_package_add");
        var package = c.Options.RequiredToken("package", RecipeRules.NpmPackageName(), "a package name such as lodash or @types/node (lower case)");
        var version = c.Options.RequiredToken("version", RecipeRules.NpmExactVersion(), "an exact version such as 4.17.21, as pkg_find shows it");
        var args = new List<string> { "install", $"{package}@{version}", "--save-exact", "--ignore-scripts", "--no-audit", "--no-fund" };
        if (c.Options.Flag("dev"))
        {
            args.Add("--save-dev");
        }

        return Npm(c, "node.package_add", $"npm install {package}@{version} --save-exact --ignore-scripts", args, project, 10, 30);
    }

    private CommandPlan PackageRemove(RecipeContext c)
    {
        var project = ReadProject(c);
        RequireNpm(project, "node_package_remove");
        var package = c.Options.RequiredToken("package", RecipeRules.NpmPackageName(), "a package name such as lodash or @types/node (lower case)");
        return Npm(c, "node.package_remove", $"npm uninstall {package} --ignore-scripts", ["uninstall", package, "--ignore-scripts", "--no-audit", "--no-fund"], project, 10, 30);
    }

    /// <summary>The project of the 'project' option (a folder, or its package.json), else the workspace folder; its package.json is read here, once.</summary>
    private static Project ReadProject(RecipeContext c)
    {
        var path = c.OptionalPath("project");
        var folder = c.Root;
        if (path is not null)
        {
            if (Directory.Exists(path))
            {
                folder = path;
            }
            else if (File.Exists(path) && Path.GetFileName(path).Equals("package.json", StringComparison.OrdinalIgnoreCase))
            {
                folder = Path.GetDirectoryName(path) ?? c.Root;
            }
            else
            {
                throw new ArgumentException("Option 'project' must be an existing project folder, or its package.json.");
            }
        }

        var manifest = Path.Combine(folder, "package.json");
        if (!File.Exists(manifest))
        {
            throw new ArgumentException($"There is no package.json in {folder}. Create the project with file_write, or point 'project' at the folder that has one.");
        }

        if (new FileInfo(manifest).Length > MaxManifestBytes)
        {
            throw new ArgumentException("package.json is too large to read.");
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifest), ManifestOptions);
            var scripts = new Dictionary<string, string>(StringComparer.Ordinal);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("scripts", out var table)
                && table.ValueKind == JsonValueKind.Object)
            {
                foreach (var script in table.EnumerateObject())
                {
                    if (script.Value.ValueKind == JsonValueKind.String)
                    {
                        scripts[script.Name] = script.Value.GetString() ?? string.Empty;
                    }
                }
            }

            var field = document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("packageManager", out var manager)
                && manager.ValueKind == JsonValueKind.String
                    ? manager.GetString()
                    : null;
            return new Project(folder, manifest, scripts, field);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"package.json is not valid JSON: {ex.Message}");
        }
    }

    private static string ScriptName(RecipeContext c, Project project, string? defaultScript)
    {
        var requested = c.Options.Token("script", RecipeRules.NpmScriptName(), "a script name of package.json");
        var name = requested ?? defaultScript ?? throw new ArgumentException("Option 'script' is required.");
        RequireScript(project, name);
        return name;
    }

    private static void RequireScript(Project project, string name)
    {
        if (!project.Scripts.ContainsKey(name))
        {
            var known = project.Scripts.Count == 0 ? "it has no scripts" : "its scripts are: " + string.Join(", ", project.Scripts.Keys.Take(20));
            throw new ArgumentException($"package.json has no script named '{name}'; {known}. Add it with file_edit_lines, or name another script with option 'script'.");
        }
    }

    /// <summary>The line the owner reads: the script name and, because the script is a command line of its own, its text.</summary>
    private static string Display(Project project, string name, IReadOnlyList<string> extra)
    {
        var body = project.Scripts[name];
        var preview = body.Length > MaxScriptPreviewChars ? body[..MaxScriptPreviewChars] + "..." : body;
        var suffix = extra.Count == 0 ? string.Empty : " -- " + string.Join(' ', extra);
        return $"npm run {name}{suffix}   # runs: {preview.ReplaceLineEndings(" ")}";
    }

    /// <summary>An argument for the script's own program: no control characters, and it cannot be an option that loads code (--require, --import, --loader, --config=...js).</summary>
    private static string CheckedArgument(string argument)
    {
        if (!Regex.IsMatch(argument, @"^[A-Za-z0-9_@/.:=,+\- \[\]]{1,200}$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)))
        {
            throw new ArgumentException($"Option 'args' has an entry that is not accepted: letters, digits and _ @ / . : = , + - [ ] and space.");
        }

        var lowered = argument.TrimStart('-').ToLowerInvariant();
        if (argument.StartsWith('-') && (lowered.StartsWith("require", StringComparison.Ordinal) || lowered.StartsWith("import", StringComparison.Ordinal)
            || lowered.StartsWith("loader", StringComparison.Ordinal) || lowered.StartsWith("experimental-loader", StringComparison.Ordinal)
            || lowered.StartsWith("eval", StringComparison.Ordinal) || lowered.StartsWith("inspect", StringComparison.Ordinal)))
        {
            throw new ArgumentException($"Option 'args' has an option that loads or evaluates code ('{argument}'); it is not accepted.");
        }

        return argument;
    }

    private string ManagerOf(Project project)
    {
        if (File.Exists(Path.Combine(project.Folder, "pnpm-lock.yaml")))
        {
            return "pnpm";
        }

        if (File.Exists(Path.Combine(project.Folder, "yarn.lock")))
        {
            return "yarn";
        }

        if (File.Exists(Path.Combine(project.Folder, "bun.lockb")) || File.Exists(Path.Combine(project.Folder, "bun.lock")))
        {
            return "bun";
        }

        var field = project.PackageManagerField;
        var at = field?.IndexOf('@', StringComparison.Ordinal) ?? -1;
        return field is not null && at > 0 ? field[..at] : "npm";
    }

    /// <summary>Installing and editing packages needs npm; another manager's lock file would be ignored and a second one created.</summary>
    private void RequireNpm(Project project, string action)
    {
        var manager = ManagerOf(project);
        if (manager != "npm" || OtherManagerLocks.Any(name => File.Exists(Path.Combine(project.Folder, name))))
        {
            throw new ArgumentException(
                $"{action} works with npm only, and this project uses {manager}. Run the {manager} command through cmd_shell (the owner approves the script), or change the project to npm.");
        }
    }

    private static void RequireModules(Project project)
    {
        if (!Directory.Exists(Path.Combine(project.Folder, "node_modules")))
        {
            throw new ArgumentException("This project has no node_modules yet. Install the dependencies with node_install first.");
        }
    }

    private static string? TypeScriptVersion(Project project)
    {
        var file = Path.Combine(project.Folder, "node_modules", "typescript", "package.json");
        try
        {
            if (!File.Exists(file) || new FileInfo(file).Length > MaxManifestBytes)
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(file), ManifestOptions);
            return document.RootElement.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.String ? version.GetString() : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>npm, started as node running npm-cli.js.</summary>
    private CommandPlan Npm(RecipeContext c, string operation, string display, List<string> npmArguments, Project project, int defaultMinutes, int maxMinutes)
    {
        var node = executables.Find("node") ?? throw new ArgumentException($"Node.js was not found on this computer. {NodeHint}");
        var cli = executables.FindNpmCli(node) ?? throw new ArgumentException("npm was not found next to node. Reinstall Node.js with npm included.");
        var args = new List<string> { cli };
        args.AddRange(npmArguments);
        return Make(c, operation, display, args, project, defaultMinutes, maxMinutes, node);
    }

    private CommandPlan Node(RecipeContext c, string operation, string display, List<string> nodeArguments, Project project, int defaultMinutes, int maxMinutes)
    {
        var node = executables.Find("node") ?? throw new ArgumentException($"Node.js was not found on this computer. {NodeHint}");
        return Make(c, operation, display, nodeArguments, project, defaultMinutes, maxMinutes, node);
    }

    private static CommandPlan Make(RecipeContext c, string operation, string display, IReadOnlyList<string> args, Project project, int defaultMinutes, int maxMinutes, string nodePath)
    {
        var minutes = c.Options.Number("timeoutMinutes", 1, maxMinutes) ?? defaultMinutes;
        return new CommandPlan(operation, "node", args, TimeSpan.FromMinutes(minutes), null, display, nodePath, project.Folder);
    }
}
