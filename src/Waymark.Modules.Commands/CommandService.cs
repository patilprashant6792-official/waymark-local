using System.Globalization;
using ModelContextProtocol;
using Waymark.Core.Commands;
using Waymark.Core.Security;
using Waymark.Core.Tools;
using Waymark.Core.Workspaces;
using Waymark.Modules.Commands.Execution;
using Waymark.Modules.Commands.Policy;
using Waymark.Modules.Commands.Recipes;
using Waymark.Modules.Commands.Reporting;

namespace Waymark.Modules.Commands;

/// <summary>
/// What the MCP tools call. Checks the request, builds the command from a recipe (or takes the shell script), hands it to the coordinator and
/// turns the result into the short report Claude reads. Every failure that Claude can correct is thrown as a message it can act on.
/// </summary>
public sealed class CommandService(
    IPathGuard guard,
    CommandCoordinator coordinator,
    ShellLocator shells,
    ProcRecipes proc,
    PythonRecipes python,
    NodeRecipes node,
    IWorkspaceStore workspaces,
    ToolPipelineOptions pipeline,
    TimeProvider time)
{
    public const string ClaudeActor = "claude";
    private const int DefaultWaitSeconds = 30;
    private const int MinCallMarginSeconds = 12;
    private const int MaxScriptChars = 20_000;
    private const int MaxPurposeChars = 300;
    private const int DefaultShellTimeoutSeconds = 300;
    private const int MaxShellTimeoutSeconds = 3600;
    private const int MaxOutputLines = 500;
    private const int MaxListed = 30;

    /// <summary>The project toolchains: a .NET action (build, test, ef_...), a Python action (py_test, py_install ...) or a Node action (node_test, node_install ...). The action's name says which; they never overlap.</summary>
    public Task<string> ProjectAsync(string workspace, string action, string purpose, string? optionsJson, int? waitSeconds, CancellationToken ct)
    {
        var name = action?.Trim().ToLowerInvariant() ?? string.Empty;
        if (PythonRecipes.Handles(name))
        {
            return RecipeAsync(workspace, purpose, waitSeconds, optionsJson, (context, token) => python.PlanAsync(name, context, token), ct);
        }

        return NodeRecipes.Handles(name)
            ? RecipeAsync(workspace, purpose, waitSeconds, optionsJson, (context, token) => node.PlanAsync(name, context, token), ct)
            : RecipeAsync(workspace, purpose, waitSeconds, optionsJson, (context, _) => Task.FromResult(DotnetRecipes.Plan(name, context)), ct);
    }

    public Task<string> GitAsync(string workspace, string action, string purpose, string? optionsJson, int? waitSeconds, CancellationToken ct) =>
        RecipeAsync(workspace, purpose, waitSeconds, optionsJson, (context, _) => Task.FromResult(GitRecipes.Plan(action, context)), ct);

    public Task<string> ProcAsync(string workspace, string action, string purpose, string? optionsJson, int? waitSeconds, CancellationToken ct) =>
        RecipeAsync(workspace, purpose, waitSeconds, optionsJson, (context, token) => proc.PlanAsync(action, context, token), ct);

    public async Task<string> ShellAsync(
        string? workspaceName,
        string script,
        string purpose,
        string? shellId,
        string? cwd,
        int? timeoutSeconds,
        int? waitSeconds,
        CancellationToken ct)
    {
        try
        {
            var cleanPurpose = CleanPurpose(purpose);
            var text = (script ?? string.Empty).Trim();
            if (text.Length == 0 || text.Length > MaxScriptChars || text.Contains('\0', StringComparison.Ordinal))
            {
                throw new ArgumentException($"The script must have 1 to {MaxScriptChars} characters and no NUL characters.");
            }

            var shell = shells.Resolve(shellId)
                ?? throw new ArgumentException(shells.Available.Count == 0
                    ? "No shell was found on this computer."
                    : $"Shell '{shellId}' is not installed. Available: {string.Join(", ", shells.Available.Select(s => s.Id))}. Leave 'shell' empty to pick automatically.");

            Workspace? workspace = string.IsNullOrWhiteSpace(workspaceName) ? null : await guard.GetWorkspaceAsync(workspaceName, ct).ConfigureAwait(false);
            var folder = ResolveFolder(workspace, cwd);
            var seconds = timeoutSeconds ?? DefaultShellTimeoutSeconds;
            if (seconds < 5 || seconds > MaxShellTimeoutSeconds)
            {
                throw new ArgumentException($"timeoutSeconds must be between 5 and {MaxShellTimeoutSeconds}.");
            }

            var submission = new CommandSubmission(
                workspace,
                workspace?.Name ?? "system",
                CommandCatalog.Get(CommandCatalog.ShellRun),
                CommandTier.Shell,
                text,
                folder,
                cleanPurpose,
                ClaudeActor,
                TimeSpan.FromSeconds(seconds),
                null,
                shell,
                text,
                RiskHints.Scan(text));
            return await SubmitAndReportAsync(submission, workspace?.Path, waitSeconds, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsRequestError(ex))
        {
            throw new McpException(ex.Message);
        }
    }

    public async Task<string> WaitAsync(long runId, int? waitSeconds, CancellationToken ct)
    {
        var run = await coordinator.WaitAsync(runId, ClampWait(waitSeconds), ct).ConfigureAwait(false)
            ?? throw new McpException($"Run #{runId} does not exist.");
        return await ReportAsync(run, ct).ConfigureAwait(false);
    }

    public async Task<string> StopAsync(long runId, CancellationToken ct)
    {
        var existing = await coordinator.GetAsync(runId, ct).ConfigureAwait(false) ?? throw new McpException($"Run #{runId} does not exist.");
        if (!existing.Status.IsActive() || !await coordinator.StopAsync(runId, ct).ConfigureAwait(false))
        {
            return $"Run #{runId} is not active (status: {existing.Status}).";
        }

        var stopped = await coordinator.WaitAsync(runId, TimeSpan.FromSeconds(8), ct).ConfigureAwait(false) ?? existing;
        return await ReportAsync(stopped, ct).ConfigureAwait(false);
    }

    public async Task<string> OutputAsync(long runId, int? fromLine, int? lines, string? grep, CancellationToken ct)
    {
        var run = await coordinator.GetAsync(runId, ct).ConfigureAwait(false) ?? throw new McpException($"Run #{runId} does not exist.");
        var plain = await coordinator.ReadPlainOutputAsync(runId, ct).ConfigureAwait(false) ?? string.Empty;
        var all = plain.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        while (all.Count > 0 && all[^1].Length == 0)
        {
            all.RemoveAt(all.Count - 1);
        }

        var count = Math.Clamp(lines ?? (string.IsNullOrWhiteSpace(grep) ? 150 : 100), 1, MaxOutputLines);
        var output = new OutputBudget(pipeline.MaxOutputChars);
        var invariant = CultureInfo.InvariantCulture;
        if (!string.IsNullOrWhiteSpace(grep))
        {
            var needle = grep.Trim();
            var hits = all.Select((text, index) => (text, number: index + 1)).Where(x => x.text.Contains(needle, StringComparison.OrdinalIgnoreCase)).ToList();
            output.TryAppendLine(string.Create(invariant, $"Run #{runId} ({run.Status}) - {hits.Count} of {all.Count} lines contain '{needle}' (showing {Math.Min(count, hits.Count)}):"));
            foreach (var (text, number) in hits.Take(count))
            {
                output.TryAppendLine(string.Create(invariant, $"{number,6} | {text}"));
            }

            return output.Build().TrimEnd();
        }

        var first = fromLine is { } requested && requested >= 1 ? requested : Math.Max(1, all.Count - count + 1);
        var window = all.Skip(first - 1).Take(count).ToList();
        output.TryAppendLine(window.Count == 0
            ? string.Create(invariant, $"Run #{runId} ({run.Status}) - there is no line {first}; the output has {all.Count} lines.")
            : string.Create(invariant, $"Run #{runId} ({run.Status}) - lines {first}-{first + window.Count - 1} of {all.Count}:"));
        for (var i = 0; i < window.Count; i++)
        {
            output.TryAppendLine(string.Create(invariant, $"{first + i,6} | {window[i]}"));
        }

        if (run.Status.IsActive())
        {
            output.TryAppendLine("(the command is still running; call again for newer lines)");
        }

        return output.Build().TrimEnd();
    }

    public async Task<string> ListAsync(string? workspaceName, int? limit, CancellationToken ct)
    {
        try
        {
            long? workspaceId = null;
            if (!string.IsNullOrWhiteSpace(workspaceName))
            {
                workspaceId = (await guard.GetWorkspaceAsync(workspaceName, ct).ConfigureAwait(false)).Id;
            }

            var take = Math.Clamp(limit ?? 10, 1, MaxListed);
            var runs = await coordinator.ListAsync(new CommandRunQuery(WorkspaceId: workspaceId, Limit: take), ct).ConfigureAwait(false);
            if (runs.Count == 0)
            {
                return "No commands have been requested yet.";
            }

            var now = time.GetUtcNow();
            var output = new OutputBudget(pipeline.MaxOutputChars);
            output.TryAppendLine(string.Create(CultureInfo.InvariantCulture, $"{runs.Count} most recent command(s), newest first:"));
            foreach (var run in runs)
            {
                output.TryAppendLine(Row(run, now));
            }

            return output.Build().TrimEnd();
        }
        catch (Exception ex) when (IsRequestError(ex))
        {
            throw new McpException(ex.Message);
        }
    }

    private async Task<string> RecipeAsync(
        string workspaceName,
        string purpose,
        int? waitSeconds,
        string? optionsJson,
        Func<RecipeContext, CancellationToken, Task<CommandPlan>> plan,
        CancellationToken ct)
    {
        try
        {
            var cleanPurpose = CleanPurpose(purpose);
            var workspace = await guard.GetWorkspaceAsync(workspaceName, ct).ConfigureAwait(false);
            var context = new RecipeContext(workspace, guard, RecipeOptions.Parse(optionsJson));
            var built = await plan(context, ct).ConfigureAwait(false);
            var root = Path.GetFullPath(workspace.Path);
            var submission = new CommandSubmission(
                workspace,
                workspace.Name,
                CommandCatalog.Get(built.Operation),
                CommandTier.Recipe,
                built.CommandLine,
                built.WorkingDirectory ?? root,
                cleanPurpose,
                ClaudeActor,
                built.Timeout,
                built,
                null,
                null,
                []);
            return await SubmitAndReportAsync(submission, root, waitSeconds, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsRequestError(ex))
        {
            throw new McpException(ex.Message);
        }
    }

    private async Task<string> SubmitAndReportAsync(CommandSubmission submission, string? workspaceRoot, int? waitSeconds, CancellationToken ct)
    {
        var result = await coordinator.SubmitAsync(submission, ct).ConfigureAwait(false);
        if (result.IsRefused)
        {
            throw new McpException(result.RefusalReason!);
        }

        var run = await coordinator.WaitAsync(result.Run.Id, ClampWait(waitSeconds), ct).ConfigureAwait(false) ?? result.Run;
        return await ReportAsync(run, ct, workspaceRoot).ConfigureAwait(false);
    }

    private async Task<string> ReportAsync(CommandRun run, CancellationToken ct, string? workspaceRoot = null)
    {
        var root = workspaceRoot;
        if (root is null && run.WorkspaceId is { } id)
        {
            var all = await workspaces.ListAsync(ct).ConfigureAwait(false);
            root = all.FirstOrDefault(w => w.Id == id)?.Path;
        }

        var plain = await coordinator.ReadPlainOutputAsync(run.Id, ct).ConfigureAwait(false) ?? string.Empty;
        return RunReport.Build(run, plain, root is null ? null : Path.GetFullPath(root), time.GetUtcNow(), pipeline.MaxOutputChars);
    }

    private TimeSpan ClampWait(int? requested)
    {
        var most = Math.Max(0, (int)pipeline.CallTimeout.TotalSeconds - MinCallMarginSeconds);
        return TimeSpan.FromSeconds(Math.Clamp(requested ?? DefaultWaitSeconds, 0, most));
    }

    private string ResolveFolder(Workspace? workspace, string? cwd)
    {
        if (workspace is not null)
        {
            var guarded = guard.Resolve(workspace, cwd);
            return Directory.Exists(guarded.AbsolutePath)
                ? guarded.AbsolutePath
                : throw new ArgumentException($"The folder '{cwd}' does not exist in the workspace.");
        }

        if (string.IsNullOrWhiteSpace(cwd))
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        if (!Path.IsPathRooted(cwd) || !Directory.Exists(cwd))
        {
            throw new ArgumentException("Without a workspace, 'cwd' must be the full path of an existing folder.");
        }

        return Path.GetFullPath(cwd);
    }

    private static string CleanPurpose(string? purpose)
    {
        var text = (purpose ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            throw new ArgumentException("'purpose' is required: say in one sentence why you run this. The owner reads it before approving.");
        }

        return text.Length <= MaxPurposeChars ? text.ReplaceLineEndings(" ") : throw new ArgumentException($"'purpose' is longer than {MaxPurposeChars} characters.");
    }

    private static bool IsRequestError(Exception ex) =>
        ex is ArgumentException or KeyNotFoundException or InvalidOperationException or FileAccessDeniedException;

    private static string Row(CommandRun run, DateTimeOffset now)
    {
        var icon = run.Status switch
        {
            CommandRunStatus.Succeeded => "✅",
            CommandRunStatus.Failed => "❌",
            CommandRunStatus.TimedOut => "⏱",
            CommandRunStatus.Cancelled => "🛑",
            CommandRunStatus.Denied => "⛔",
            CommandRunStatus.WaitingApproval => "⏳",
            CommandRunStatus.Queued => "⏳",
            _ => "🔄",
        };
        var line = run.CommandLine.ReplaceLineEndings(" ");
        var shown = line.Length > 90 ? line[..90] + "..." : line;
        var age = now - run.RequestedUtc;
        var ago = age.TotalMinutes >= 1 ? string.Create(CultureInfo.InvariantCulture, $"{(int)age.TotalMinutes} min ago") : "just now";
        return string.Create(CultureInfo.InvariantCulture, $"#{run.Id} {icon} {run.Status} {run.Operation} [{run.WorkspaceName}] {shown} ({ago})");
    }
}
