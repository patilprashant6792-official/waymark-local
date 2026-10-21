namespace Waymark.Modules.Commands.Recipes;

/// <summary>Turns a dotnet action and its checked options into a command plan.</summary>
public static class DotnetRecipes
{
    public static readonly IReadOnlyList<string> Actions =
    [
        "build", "rebuild", "clean", "restore", "test", "format_check", "format", "info", "list", "new", "sln", "reference",
        "package_add", "package_remove", "tool_restore", "ef_list", "ef_migrations_add", "ef_migrations_remove", "ef_database_update", "run",
    ];

    private static readonly string[] Verbosities = ["quiet", "minimal", "normal", "detailed"];

    // The templates Claude may create from. Anything else (install, uninstall, search ...) would be a different dotnet command.
    private static readonly string[] Templates =
    [
        "classlib", "console", "webapi", "web", "mvc", "razor", "blazor", "blazorwasm", "worker", "grpc", "razorclasslib", "xunit", "xunit3", "mstest",
        "nunit", "sln", "slnx", "gitignore", "editorconfig", "globaljson", "nugetconfig", "tool-manifest", "wpf", "winforms", "wpflib", "winformslib",
    ];

    public static CommandPlan Plan(string action, RecipeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var plan = Build(action?.Trim().ToLowerInvariant() ?? string.Empty, context);
        context.Options.EnsureNoUnknown();
        return plan;
    }

    private static CommandPlan Build(string action, RecipeContext c) => action switch
    {
        "build" => Compile(c, "dotnet.build", ["build"], 15),
        "rebuild" => Compile(c, "dotnet.rebuild", ["build", "--no-incremental"], 15),
        "clean" => Compile(c, "dotnet.clean", ["clean"], 5),
        "restore" => Compile(c, "dotnet.restore", ["restore"], 10),
        "test" => Test(c),
        "format_check" => Format(c, "dotnet.format_check", verify: true),
        "format" => Format(c, "dotnet.format", verify: false),
        "info" => Simple(c, "dotnet.info", ["--info"], 2),
        "list" => List(c),
        "new" => New(c),
        "sln" => Solution(c),
        "reference" => Reference(c),
        "package_add" => PackageAdd(c),
        "package_remove" => PackageRemove(c),
        "tool_restore" => Simple(c, "dotnet.tool_restore", ["tool", "restore"], 10),
        "ef_list" => EfList(c),
        "ef_migrations_add" => EfAdd(c),
        "ef_migrations_remove" => EfRemove(c),
        "ef_database_update" => EfUpdate(c),
        "run" => Run(c),
        _ => throw new ArgumentException($"Unknown dotnet action '{action}'. Actions: {string.Join(", ", Actions)}."),
    };

    private static CommandPlan Compile(RecipeContext c, string operation, List<string> args, int defaultMinutes)
    {
        AddTarget(c, args);

        // dotnet restore has no configuration or framework option.
        if (operation != "dotnet.restore")
        {
            AddConfiguration(c, args);
            AddFramework(c, args);
        }

        if (operation is "dotnet.build" or "dotnet.rebuild" && c.Options.Flag("noRestore"))
        {
            args.Add("--no-restore");
        }

        AddVerbosity(c, args);
        args.Add("-nodeReuse:false");
        args.Add("-p:UseSharedCompilation=false");
        args.Add("-tl:off");
        args.Add("-clp:ForceConsoleColor");
        return Make(c, operation, args, defaultMinutes, 120);
    }

    private static CommandPlan Test(RecipeContext c)
    {
        var args = new List<string> { "test" };
        AddTarget(c, args);
        AddConfiguration(c, args);
        AddFramework(c, args);
        var filter = c.Options.Token("filter", RecipeRules.TestFilter(), "letters, digits and the characters . , ( ) = ! ~ | & < > - and space, at most 200");
        if (filter is not null)
        {
            args.Add("--filter");
            args.Add(filter);
        }

        if (c.Options.Flag("noBuild"))
        {
            args.Add("--no-build");
        }

        if (c.Options.Flag("noRestore"))
        {
            args.Add("--no-restore");
        }

        AddVerbosity(c, args);
        return Make(c, "dotnet.test", args, 30, 120);
    }

    private static CommandPlan Format(RecipeContext c, string operation, bool verify)
    {
        var args = new List<string> { "format" };
        var scope = c.Options.OneOf("scope", "whitespace", "style", "analyzers");
        if (scope is not null)
        {
            args.Add(scope);
        }

        AddTarget(c, args);
        if (verify)
        {
            args.Add("--verify-no-changes");
        }

        args.Add("--no-restore");
        return Make(c, operation, args, 10, 60);
    }

    private static CommandPlan List(RecipeContext c)
    {
        var kind = c.Options.OneOf("kind", "projects", "references", "packages") ?? throw new ArgumentException("Option 'kind' is required: projects, references or packages.");
        List<string> args;
        switch (kind)
        {
            case "projects":
                args = ["sln", c.ExistingSolution("solution"), "list"];
                break;
            case "references":
                args = ["list", c.ExistingProject("project"), "reference"];
                break;
            default:
                args = ["list", c.ExistingProject("project"), "package"];
                var filter = c.Options.OneOf("filter", "outdated", "vulnerable", "deprecated");
                if (filter is not null)
                {
                    args.Add("--" + filter);
                }

                if (c.Options.Flag("includeTransitive"))
                {
                    args.Add("--include-transitive");
                }

                break;
        }

        return Make(c, "dotnet.list", args, 5, 30);
    }

    private static CommandPlan New(RecipeContext c)
    {
        var template = (c.Options.RequiredText("template", 40)).ToLowerInvariant();
        if (!Templates.Contains(template, StringComparer.Ordinal))
        {
            throw new ArgumentException($"Template '{template}' is not allowed. Allowed: {string.Join(", ", Templates)}.");
        }

        var args = new List<string> { "new", template };
        var name = c.Options.Token("name", RecipeRules.ProjectName(), "a name made of letters, digits, '_', '.' and '-' that starts with a letter or '_', at most 100");
        if (name is not null)
        {
            args.Add("-n");
            args.Add(name);
        }

        var output = c.OptionalPath("output");
        if (output is not null)
        {
            args.Add("-o");
            args.Add(output);
        }

        var framework = c.Options.Token("framework", RecipeRules.Framework(), "a target framework such as net10.0");
        if (framework is not null)
        {
            args.Add("-f");
            args.Add(framework);
        }

        return Make(c, "dotnet.new", args, 5, 30);
    }

    private static CommandPlan Solution(RecipeContext c)
    {
        var change = c.Options.OneOf("change", "add", "remove") ?? throw new ArgumentException("Option 'change' is required: add or remove.");
        var solution = c.ExistingSolution("solution");
        var projects = c.Options.List("projects", 50, 400);
        if (projects.Count == 0)
        {
            throw new ArgumentException("Option 'projects' needs at least one project path.");
        }

        var args = new List<string> { "sln", solution, change };
        foreach (var project in projects)
        {
            args.Add(c.RequiredAbsolute(project));
        }

        return Make(c, "dotnet.sln", args, 5, 30);
    }

    private static CommandPlan Reference(RecipeContext c)
    {
        var change = c.Options.OneOf("change", "add", "remove") ?? throw new ArgumentException("Option 'change' is required: add or remove.");
        var project = c.ExistingProject("project");
        var references = c.Options.List("references", 50, 400);
        if (references.Count == 0)
        {
            throw new ArgumentException("Option 'references' needs at least one project path.");
        }

        var args = new List<string> { change, project, "reference" };
        foreach (var reference in references)
        {
            args.Add(c.RequiredAbsolute(reference));
        }

        return Make(c, "dotnet.reference", args, 5, 30);
    }

    private static CommandPlan PackageAdd(RecipeContext c)
    {
        var project = c.ExistingProject("project");
        var package = c.Options.RequiredToken("package", RecipeRules.PackageId(), "a NuGet package id (letters, digits, '.', '_' and '-')");
        var args = new List<string> { "add", project, "package", package };
        var version = c.Options.Token("version", RecipeRules.PackageVersion(), "a version such as 13.0.3 or 2.0.0-beta.1");
        if (version is not null)
        {
            args.Add("--version");
            args.Add(version);
        }

        if (c.Options.Flag("prerelease"))
        {
            args.Add("--prerelease");
        }

        return Make(c, "dotnet.package_add", args, 10, 30);
    }

    private static CommandPlan PackageRemove(RecipeContext c)
    {
        var project = c.ExistingProject("project");
        var package = c.Options.RequiredToken("package", RecipeRules.PackageId(), "a NuGet package id (letters, digits, '.', '_' and '-')");
        return Make(c, "dotnet.package_remove", ["remove", project, "package", package], 5, 30);
    }

    private static CommandPlan EfList(RecipeContext c)
    {
        var kind = c.Options.OneOf("kind", "migrations", "dbcontexts") ?? "migrations";
        var args = new List<string> { "ef", kind == "migrations" ? "migrations" : "dbcontext", "list" };
        AddEfProjects(c, args);
        return Make(c, "dotnet.ef_read", args, 5, 30);
    }

    private static CommandPlan EfAdd(RecipeContext c)
    {
        var name = c.Options.RequiredToken("name", RecipeRules.MigrationName(), "a C# identifier of letters, digits and '_', at most 100");
        var args = new List<string> { "ef", "migrations", "add", name };
        AddEfProjects(c, args);
        var output = c.OptionalPath("outputDir");
        if (output is not null)
        {
            args.Add("--output-dir");
            args.Add(output);
        }

        return Make(c, "dotnet.ef_migrations_add", args, 10, 30);
    }

    private static CommandPlan EfRemove(RecipeContext c)
    {
        var args = new List<string> { "ef", "migrations", "remove" };
        AddEfProjects(c, args);
        return Make(c, "dotnet.ef_migrations_remove", args, 10, 30);
    }

    private static CommandPlan EfUpdate(RecipeContext c)
    {
        var args = new List<string> { "ef", "database", "update" };
        var target = c.Options.Token("migration", RecipeRules.MigrationName(), "a migration name, or 0 to undo all");
        var zero = c.Options.Flag("revertAll");
        if (target is not null && zero)
        {
            throw new ArgumentException("Give either 'migration' or 'revertAll', not both.");
        }

        if (target is not null)
        {
            args.Add(target);
        }
        else if (zero)
        {
            args.Add("0");
        }

        AddEfProjects(c, args);
        return Make(c, "dotnet.ef_database_update", args, 10, 60);
    }

    private static CommandPlan Run(RecipeContext c)
    {
        var project = c.ExistingProject("project");
        var args = new List<string> { "run", "--project", project };
        AddConfiguration(c, args);
        AddFramework(c, args);
        var profile = c.Options.Token("launchProfile", RecipeRules.LaunchProfile(), "a launch profile name");
        if (profile is null)
        {
            args.Add("--no-launch-profile");
        }
        else
        {
            args.Add("--launch-profile");
            args.Add(profile);
        }

        if (c.Options.Flag("noBuild"))
        {
            args.Add("--no-build");
        }

        var appArguments = c.Options.List("appArgs", 20, 200);
        if (appArguments.Count > 0)
        {
            args.Add("--");
            args.AddRange(appArguments);
        }

        return Make(c, "dotnet.run", args, 30, 480);
    }

    private static CommandPlan Simple(RecipeContext c, string operation, List<string> args, int defaultMinutes) =>
        Make(c, operation, args, defaultMinutes, 60);

    private static void AddTarget(RecipeContext c, List<string> args)
    {
        var target = c.BuildTarget("target");
        if (target is not null)
        {
            args.Add(target);
        }
    }

    private static void AddConfiguration(RecipeContext c, List<string> args)
    {
        var configuration = c.Options.Token("configuration", RecipeRules.Configuration(), "a configuration name such as Debug or Release");
        if (configuration is not null)
        {
            args.Add("-c");
            args.Add(configuration);
        }
    }

    private static void AddFramework(RecipeContext c, List<string> args)
    {
        var framework = c.Options.Token("framework", RecipeRules.Framework(), "a target framework such as net10.0");
        if (framework is not null)
        {
            args.Add("-f");
            args.Add(framework);
        }
    }

    private static void AddVerbosity(RecipeContext c, List<string> args)
    {
        var verbosity = c.Options.OneOf("verbosity", Verbosities) ?? "minimal";
        args.Add("-v");
        args.Add(verbosity);
    }

    private static void AddEfProjects(RecipeContext c, List<string> args)
    {
        args.Add("--project");
        args.Add(c.ExistingProject("project"));
        var startup = c.OptionalPath("startupProject");
        if (startup is not null)
        {
            args.Add("--startup-project");
            args.Add(startup);
        }

        var dbContext = c.Options.Token("context", RecipeRules.ContextName(), "a DbContext class name");
        if (dbContext is not null)
        {
            args.Add("--context");
            args.Add(dbContext);
        }

        if (c.Options.Flag("noBuild"))
        {
            args.Add("--no-build");
        }
    }

    private static CommandPlan Make(RecipeContext c, string operation, List<string> args, int defaultMinutes, int maxMinutes)
    {
        var minutes = c.Options.Number("timeoutMinutes", 1, maxMinutes) ?? defaultMinutes;
        return new CommandPlan(operation, "dotnet", args, TimeSpan.FromMinutes(minutes));
    }
}
