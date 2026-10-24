using System.ComponentModel;
using ModelContextProtocol.Server;
using Waymark.Core.Tools;

namespace Waymark.Modules.Commands.Tools;

/// <summary>The tools that start commands. They are Confirm tools: off until the owner switches them on, and each operation has its own Off/Ask/Auto gate on the Commands page.</summary>
[McpServerToolType]
public sealed class CommandRunTools(CommandService service)
{
    private const string WorkspaceHelp = "Workspace name as shown in Waymark (a single workspace; '*' is not accepted)";
    private const string PurposeHelp = "One sentence: why you run this. The owner reads it before approving, sees it live, and it is kept in the audit log.";
    private const string WaitHelp = "Seconds to wait for the result before returning a run id (default 30, at most about 45). The command keeps running; use cmd_wait to wait longer.";

    [McpServerTool(Name = "cmd_dotnet", ReadOnly = false, Destructive = false, Idempotent = false)]
    [ToolGate(ToolGate.Confirm)]
    [Description(
        "Run a .NET command in a workspace as a typed action (no shell). The owner watches it live in the Waymark Console. Some actions run at once, others wait for the owner's approval - the result says which.\n" +
        "ACTIONS and their optionsJson keys (* = required, all paths relative to the workspace, unknown keys are refused):\n" +
        "  build, rebuild, clean: target, configuration, framework, noRestore, verbosity (quiet|minimal|normal|detailed), timeoutMinutes\n" +
        "  restore: target\n" +
        "  test: target, configuration, framework, filter, noBuild, noRestore\n" +
        "  format_check, format: scope (whitespace|style|analyzers), target\n" +
        "  info: -\n" +
        "  list: kind* (projects|references|packages), solution (for projects), project (for the others), filter (outdated|vulnerable|deprecated), includeTransitive\n" +
        "  new: template* (classlib, console, webapi, web, mvc, blazor, worker, xunit, xunit3, mstest, nunit, sln, slnx, gitignore, editorconfig, globaljson, nugetconfig, tool-manifest ...), name, output (folder), framework\n" +
        "  sln: change* (add|remove), solution*, projects* (array)\n" +
        "  reference: change* (add|remove), project*, references* (array)\n" +
        "  package_add: project*, package*, version, prerelease\n" +
        "  package_remove: project*, package*\n" +
        "  tool_restore: -\n" +
        "  ef_list: kind (migrations|dbcontexts), project*, startupProject, context\n" +
        "  ef_migrations_add: name*, project*, startupProject, context, outputDir\n" +
        "  ef_migrations_remove: project*, startupProject, context\n" +
        "  ef_database_update: project*, startupProject, context, migration or revertAll\n" +
        "  run: project*, configuration, launchProfile, noBuild, appArgs (array), timeoutMinutes (the app keeps running until stopped)\n" +
        "When 'target' is omitted the only .sln/.slnx file in the workspace root is used.\n" +
        "RETURNS: result, compiler errors and warnings (workspace-relative paths), test summary and the last output lines. If it takes longer than waitSeconds you get a run id for cmd_wait / cmd_output.")]
    public Task<string> Dotnet(
        [Description(WorkspaceHelp)] string workspace,
        [Description("One of: build, rebuild, clean, restore, test, format_check, format, info, list, new, sln, reference, package_add, package_remove, tool_restore, ef_list, ef_migrations_add, ef_migrations_remove, ef_database_update, run")]
        string action,
        [Description(PurposeHelp)] string purpose,
        [Description("JSON object with the options of the action, for example {\"configuration\":\"Release\",\"noRestore\":true}. Omit when none are needed.")]
        string? optionsJson = null,
        [Description(WaitHelp)] int? waitSeconds = null,
        CancellationToken cancellationToken = default) =>
        service.DotnetAsync(workspace, action, purpose, optionsJson, waitSeconds, cancellationToken);

    [McpServerTool(Name = "cmd_git", ReadOnly = false, Destructive = false, Idempotent = false)]
    [ToolGate(ToolGate.Confirm)]
    [Description(
        "Run a git command in a workspace as a typed action (no shell, no pager, no external diff or filter programs). Nothing can force-push. Reading is automatic; changes wait for the owner unless the owner set them to Auto.\n" +
        "ACTIONS and optionsJson keys (* = required, paths relative to the workspace):\n" +
        "  status: -\n" +
        "  diff: staged, stat, revision, paths (array)\n" +
        "  log: count (1-200, default 20), oneline (default true), stat, revision, paths (array)\n" +
        "  show: revision*, stat\n" +
        "  branches, stashes, remotes: -\n" +
        "  add: paths (array) or all\n" +
        "  commit: message*, all\n" +
        "  branch_create: name*, from, switchTo\n" +
        "  branch_switch: name*\n" +
        "  stash_push: message, includeUntracked\n" +
        "  stash_pop: -\n" +
        "  restore: paths* (array), staged\n" +
        "  pull: remote, branch (fast-forward only)\n" +
        "  push: remote (default origin), branch, setUpstream\n" +
        "  reset_hard: revision (default HEAD)   - switched off unless the owner enables it\n" +
        "  clean: dryRun (default true), paths (array)   - switched off unless the owner enables it\n" +
        "  branch_delete: name*   - switched off unless the owner enables it\n" +
        "RETURNS: result and the output of git.")]
    public Task<string> Git(
        [Description(WorkspaceHelp)] string workspace,
        [Description("One of: status, diff, log, show, branches, stashes, remotes, add, commit, branch_create, branch_switch, stash_push, stash_pop, restore, pull, push, reset_hard, clean, branch_delete")]
        string action,
        [Description(PurposeHelp)] string purpose,
        [Description("JSON object with the options of the action, for example {\"message\":\"Fix null check\"}. Omit when none are needed.")]
        string? optionsJson = null,
        [Description(WaitHelp)] int? waitSeconds = null,
        CancellationToken cancellationToken = default) =>
        service.GitAsync(workspace, action, purpose, optionsJson, waitSeconds, cancellationToken);

    [McpServerTool(Name = "cmd_proc", ReadOnly = false, Destructive = false, Idempotent = false)]
    [ToolGate(ToolGate.Confirm)]
    [Description(
        "Process and clean-up helpers for a workspace.\n" +
        "ACTIONS and optionsJson keys (* = required):\n" +
        "  ports: port (show only that port)   - which program listens on which TCP port\n" +
        "  stop_port: port*   - ends the program on that port; only a program that runs from the workspace or a dotnet program, never Waymark\n" +
        "  build_server: -   - dotnet build-server shutdown, frees files locked by compiler servers\n" +
        "  clean_outputs: path (folder, default the workspace), dryRun   - deletes bin and obj folders that sit next to a project file\n" +
        "RETURNS: result and what was done.")]
    public Task<string> Proc(
        [Description(WorkspaceHelp)] string workspace,
        [Description("One of: ports, stop_port, build_server, clean_outputs")] string action,
        [Description(PurposeHelp)] string purpose,
        [Description("JSON object with the options of the action, for example {\"port\":5000}. Omit when none are needed.")]
        string? optionsJson = null,
        [Description(WaitHelp)] int? waitSeconds = null,
        CancellationToken cancellationToken = default) =>
        service.ProcAsync(workspace, action, purpose, optionsJson, waitSeconds, cancellationToken);

    [McpServerTool(Name = "cmd_shell", ReadOnly = false, Destructive = true, Idempotent = false)]
    [ToolGate(ToolGate.Confirm)]
    [Description(
        "Run a script in the computer's shell (PowerShell, cmd or bash - chosen automatically unless you name one). This can do anything the owner's user account can do, " +
        "so EVERY call waits for the owner to read the script and approve it in the Waymark admin UI; it can never run by itself. The owner may edit the script before approving.\n" +
        "Prefer the typed tools (cmd_dotnet, cmd_git, cmd_proc) when one fits: they are checked and can run without waiting.\n" +
        "Write the script for the shell that will run it; a short script that does one thing is approved faster. Nothing can be typed to it while it runs.\n" +
        "RETURNS: result and the end of the output; waits up to waitSeconds (the owner needs time to approve - use cmd_wait).")]
    public Task<string> Shell(
        [Description("The script text.")] string script,
        [Description(PurposeHelp)] string purpose,
        [Description("Optional workspace name. With it the script starts in the workspace folder; without it the script is a system script and starts in the home folder.")]
        string? workspace = null,
        [Description("Optional shell: pwsh, powershell, cmd, bash or sh. Leave empty to let Waymark pick the best one installed.")]
        string? shell = null,
        [Description("Optional start folder: relative to the workspace, or a full path when no workspace is given.")]
        string? cwd = null,
        [Description("Time limit in seconds (5-3600, default 300).")] int? timeoutSeconds = null,
        [Description(WaitHelp)] int? waitSeconds = null,
        CancellationToken cancellationToken = default) =>
        service.ShellAsync(workspace, script, purpose, shell, cwd, timeoutSeconds, waitSeconds, cancellationToken);
}
