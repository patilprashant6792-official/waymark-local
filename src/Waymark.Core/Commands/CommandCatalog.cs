namespace Waymark.Core.Commands;

/// <summary>
/// One operation Claude can ask for. <see cref="Mutating"/> operations run one at a time per workspace (two builds would fight over obj/).
/// <see cref="DriftSensitive"/> operations load project or repository files that can run code (MSBuild targets, git hooks), so they ask
/// again when those files changed since the owner last approved a command. <see cref="MaxGate"/> caps what the owner may choose.
/// </summary>
public sealed record OperationInfo(
    string Key,
    string Family,
    string Label,
    string Description,
    CommandGate Default,
    bool Mutating,
    bool DriftSensitive,
    CommandGate MaxGate = CommandGate.Auto);

/// <summary>The one list of operations, shared by the tools, the policy and the admin page.</summary>
public static class CommandCatalog
{
    /// <summary>Workspace id used for a rule that applies to every workspace.</summary>
    public const long AllWorkspaces = 0;

    public const string DotnetFamily = "dotnet";
    public const string GitFamily = "git";
    public const string ProcessFamily = "processes";
    public const string ShellFamily = "shell";

    public const string ShellRun = "shell.run";

    private static readonly IReadOnlyList<OperationInfo> All =
    [
        new("dotnet.build", DotnetFamily, "Build", "dotnet build", CommandGate.Auto, true, true),
        new("dotnet.rebuild", DotnetFamily, "Rebuild", "dotnet clean, then build", CommandGate.Auto, true, true),
        new("dotnet.clean", DotnetFamily, "Clean", "dotnet clean", CommandGate.Auto, true, true),
        new("dotnet.restore", DotnetFamily, "Restore", "dotnet restore", CommandGate.Auto, true, true),
        new("dotnet.test", DotnetFamily, "Test", "dotnet test (runs the test code, which can be code Claude has just written)", CommandGate.Ask, true, true),
        new("dotnet.format_check", DotnetFamily, "Check formatting", "dotnet format --verify-no-changes", CommandGate.Auto, false, true),
        new("dotnet.format", DotnetFamily, "Format", "dotnet format (changes files)", CommandGate.Ask, true, true),
        new("dotnet.info", DotnetFamily, "SDK info", "SDKs, runtimes and the dotnet version", CommandGate.Auto, false, false),
        new("dotnet.list", DotnetFamily, "List", "Projects of a solution, references and packages of a project", CommandGate.Auto, false, true),
        new("dotnet.new", DotnetFamily, "New project or file", "dotnet new (class library, console app, tests, solution ...)", CommandGate.Ask, true, false),
        new("dotnet.sln", DotnetFamily, "Solution projects", "Add or remove projects in a solution", CommandGate.Ask, true, false),
        new("dotnet.reference", DotnetFamily, "Project references", "Add or remove project references", CommandGate.Ask, true, false),
        new("dotnet.package_add", DotnetFamily, "Add a package", "dotnet add package", CommandGate.Ask, true, false),
        new("dotnet.package_remove", DotnetFamily, "Remove a package", "dotnet remove package", CommandGate.Ask, true, false),
        new("dotnet.tool_restore", DotnetFamily, "Restore local tools", "dotnet tool restore (downloads tools)", CommandGate.Ask, true, false),
        new("dotnet.ef_read", DotnetFamily, "EF Core, read only", "List migrations, show DbContext info (starts the app's startup code)", CommandGate.Ask, false, true),
        new("dotnet.ef_migrations_add", DotnetFamily, "EF Core, add migration", "dotnet ef migrations add", CommandGate.Ask, true, true),
        new("dotnet.ef_migrations_remove", DotnetFamily, "EF Core, remove migration", "dotnet ef migrations remove", CommandGate.Ask, true, true),
        new("dotnet.ef_database_update", DotnetFamily, "EF Core, update database", "dotnet ef database update (changes a database)", CommandGate.Ask, true, true),
        new("dotnet.run", DotnetFamily, "Run the app", "dotnet run (stays running until stopped)", CommandGate.Ask, true, true),

        new("git.read", GitFamily, "Read the repository", "status, diff, log, show, blame, branches, stashes", CommandGate.Auto, false, true),
        new("git.add", GitFamily, "Stage files", "git add", CommandGate.Ask, true, true),
        new("git.commit", GitFamily, "Commit", "git commit (runs commit hooks)", CommandGate.Ask, true, true),
        new("git.branch", GitFamily, "Create or switch branch", "git branch, git switch", CommandGate.Ask, true, true),
        new("git.stash", GitFamily, "Stash", "git stash push or pop", CommandGate.Ask, true, true),
        new("git.restore", GitFamily, "Restore files", "git restore (discards changes to those files)", CommandGate.Ask, true, true),
        new("git.pull", GitFamily, "Pull", "git pull --ff-only", CommandGate.Ask, true, true),
        new("git.push", GitFamily, "Push", "git push (sends commits off this machine)", CommandGate.Ask, true, true),
        new("git.reset_hard", GitFamily, "Reset hard", "git reset --hard (discards all changes)", CommandGate.Off, true, true),
        new("git.clean", GitFamily, "Clean untracked files", "git clean -fd (deletes untracked files)", CommandGate.Off, true, true),
        new("git.branch_delete", GitFamily, "Delete a branch", "git branch -D", CommandGate.Off, true, true),

        new("proc.ports", ProcessFamily, "List listening ports", "Which program listens on which port", CommandGate.Auto, false, false),
        new("proc.stop_port", ProcessFamily, "Stop the program on a port", "Ends the program that listens on one port", CommandGate.Ask, true, false),
        new("proc.build_server", ProcessFamily, "Stop build servers", "dotnet build-server shutdown (frees locked files)", CommandGate.Auto, false, false),
        new("proc.clean_outputs", ProcessFamily, "Delete bin and obj", "Deletes the bin and obj folders under a path", CommandGate.Ask, true, false),

        new(ShellRun, ShellFamily, "System shell", "Any script in PowerShell, cmd or bash. Always approved by you, never automatic.", CommandGate.Ask, true, true, CommandGate.Ask),
    ];

    private static readonly Dictionary<string, OperationInfo> ByKey = All.ToDictionary(o => o.Key, StringComparer.Ordinal);

    public static IReadOnlyList<OperationInfo> Operations => All;

    public static OperationInfo Get(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return ByKey.TryGetValue(key, out var info) ? info : throw new KeyNotFoundException($"Unknown command operation '{key}'.");
    }

    public static bool TryGet(string key, out OperationInfo? info)
    {
        ArgumentNullException.ThrowIfNull(key);
        return ByKey.TryGetValue(key, out info);
    }

    /// <summary>The gate that applies: the workspace's own setting, else the setting for all workspaces, else the default, never above the operation's cap.</summary>
    public static CommandGate Resolve(OperationInfo operation, long? workspaceId, IReadOnlyList<CommandGateSetting> settings)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(settings);
        CommandGate? chosen = null;
        if (workspaceId is { } id)
        {
            chosen = settings.FirstOrDefault(s => s.WorkspaceId == id && s.Operation == operation.Key)?.Gate;
        }

        chosen ??= settings.FirstOrDefault(s => s.WorkspaceId == AllWorkspaces && s.Operation == operation.Key)?.Gate;
        var gate = chosen ?? operation.Default;
        return gate > operation.MaxGate ? operation.MaxGate : gate;
    }
}
