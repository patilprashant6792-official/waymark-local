using System.Diagnostics;
using System.Globalization;
using System.Text;
using Waymark.Modules.Commands.Execution;

namespace Waymark.Modules.Commands.Recipes;

/// <summary>
/// Process and clean-up operations. Listing ports, ending a program and deleting bin and obj are done by Waymark itself rather than through a shell,
/// so what they may touch is decided here: a program is ended only when it runs from the workspace or is a dotnet host, never Waymark itself.
/// </summary>
public sealed class ProcRecipes(ListeningPorts ports)
{
    public static readonly IReadOnlyList<string> Actions = ["ports", "stop_port", "build_server", "clean_outputs"];

    private static readonly string[] ProjectPatterns = ["*.csproj", "*.fsproj", "*.vbproj"];
    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase) { "node_modules", ".git", ".vs", ".idea" };
    private static readonly TimeSpan KillWait = TimeSpan.FromSeconds(5);

    public async Task<CommandPlan> PlanAsync(string action, RecipeContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var plan = await BuildAsync(action?.Trim().ToLowerInvariant() ?? string.Empty, context, ct).ConfigureAwait(false);
        context.Options.EnsureNoUnknown();
        return plan;
    }

    private Task<CommandPlan> BuildAsync(string action, RecipeContext c, CancellationToken ct) => action switch
    {
        "ports" => Task.FromResult(Ports(c)),
        "stop_port" => StopPortAsync(c, ct),
        "build_server" => Task.FromResult(BuildServer(c)),
        "clean_outputs" => Task.FromResult(CleanOutputs(c)),
        _ => throw new ArgumentException($"Unknown proc action '{action}'. Actions: {string.Join(", ", Actions)}."),
    };

    private CommandPlan Ports(RecipeContext c)
    {
        var port = c.Options.Number("port", 1, 65535);
        var display = port is null ? "list listening ports" : $"list the program on port {port}";
        return new CommandPlan("proc.ports", "waymark", [], TimeSpan.FromMinutes(1), async (write, stop) =>
        {
            var owners = (await ports.ListAsync(stop).ConfigureAwait(false)).Where(o => port is null || o.Port == port).ToList();
            write(owners.Count == 0 ? "No listening ports found.\n" : Table(owners));
            return 0;
        }, display);
    }

    private async Task<CommandPlan> StopPortAsync(RecipeContext c, CancellationToken ct)
    {
        var port = c.Options.Number("port", 1, 65535) ?? throw new ArgumentException("Option 'port' is required.");
        var root = c.Root;
        var current = (await ports.ListAsync(ct).ConfigureAwait(false)).Where(o => o.Port == port).DistinctBy(o => o.Pid).ToList();
        var who = current.Count == 0
            ? "nothing listens now"
            : string.Join(", ", current.Select(o => string.Create(CultureInfo.InvariantCulture, $"{(o.ProcessName.Length == 0 ? "unknown" : o.ProcessName)} pid {o.Pid}")));
        var display = string.Create(CultureInfo.InvariantCulture, $"stop the program on port {port} ({who})");
        return new CommandPlan("proc.stop_port", "waymark", [], TimeSpan.FromMinutes(1), async (write, stop) =>
        {
            var owners = (await ports.ListAsync(stop).ConfigureAwait(false)).Where(o => o.Port == port).DistinctBy(o => o.Pid).ToList();
            if (owners.Count == 0)
            {
                write($"Nothing listens on port {port}.\n");
                return 0;
            }

            var failed = 0;
            foreach (var owner in owners)
            {
                if (!await TryStopAsync(owner, root, write, stop).ConfigureAwait(false))
                {
                    failed++;
                }
            }

            return failed == 0 ? 0 : 1;
        }, display);
    }

    private static CommandPlan BuildServer(RecipeContext c)
    {
        var minutes = c.Options.Number("timeoutMinutes", 1, 10) ?? 2;
        return new CommandPlan("proc.build_server", "dotnet", ["build-server", "shutdown"], TimeSpan.FromMinutes(minutes));
    }

    private static CommandPlan CleanOutputs(RecipeContext c)
    {
        var start = c.OptionalPath("path") ?? c.Root;
        var dryRun = c.Options.Flag("dryRun");
        var relative = Path.GetRelativePath(c.Root, start).Replace('\\', '/');
        var where = relative.Length == 0 || relative == "." ? "." : relative;
        var display = (dryRun ? "list" : "delete") + " the bin and obj folders under " + where;
        return new CommandPlan("proc.clean_outputs", "waymark", [], TimeSpan.FromMinutes(5), (write, stop) => Task.Run(() => DeleteOutputs(start, dryRun, write, stop), stop), display);
    }

    private static string Table(IReadOnlyList<PortOwner> owners)
    {
        var text = new StringBuilder();
        text.AppendLine("PORT   PID     PROGRAM            ADDRESS");
        foreach (var owner in owners)
        {
            text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"{owner.Port,-6} {owner.Pid,-7} {owner.ProcessName,-18} {owner.Address}"));
        }

        return text.ToString();
    }

    private static async Task<bool> TryStopAsync(PortOwner owner, string workspaceRoot, Action<string> write, CancellationToken stop)
    {
        var label = string.Create(CultureInfo.InvariantCulture, $"{(owner.ProcessName.Length == 0 ? "unknown" : owner.ProcessName)} (pid {owner.Pid})");
        if (owner.Pid <= 4 || owner.Pid == Environment.ProcessId)
        {
            write($"Refused: {label} is not a program Waymark may end.\n");
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(owner.Pid);
            var path = TryGetPath(process);
            var isDotnetHost = string.Equals(process.ProcessName, "dotnet", StringComparison.OrdinalIgnoreCase);
            if (!isDotnetHost && (path is null || !IsInside(workspaceRoot, path)))
            {
                write($"Refused: {label} does not run from this workspace and is not a dotnet program ({path ?? "path unknown"}). Ask for a shell command if it must be ended.\n");
                return false;
            }

            process.Kill(entireProcessTree: true);
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(stop);
            wait.CancelAfter(KillWait);
            await process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
            write($"Ended {label}.\n");
            return true;
        }
        catch (ArgumentException)
        {
            write($"{label} had already ended.\n");
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            write($"Could not end {label}: {ex.Message}\n");
            return false;
        }
    }

    private static string? TryGetPath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    private static bool IsInside(string root, string path)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(prefix, comparison);
    }

    // Only bin and obj folders that sit next to a project file; a bin folder of anything else is left alone.
    private static int DeleteOutputs(string start, bool dryRun, Action<string> write, CancellationToken stop)
    {
        var pending = new Stack<string>();
        pending.Push(start);
        var found = 0;
        var failed = 0;
        while (pending.Count > 0)
        {
            stop.ThrowIfCancellationRequested();
            var folder = pending.Pop();
            foreach (var child in SafeFolders(folder))
            {
                var name = Path.GetFileName(child);
                if (SkippedFolders.Contains(name) || (File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                var isOutput = name.Equals("bin", StringComparison.OrdinalIgnoreCase) || name.Equals("obj", StringComparison.OrdinalIgnoreCase);
                if (!isOutput || !HasProjectFile(folder))
                {
                    pending.Push(child);
                    continue;
                }

                found++;
                var shown = Path.GetRelativePath(start, child).Replace('\\', '/');
                if (dryRun)
                {
                    write($"Would delete {shown}\n");
                    continue;
                }

                try
                {
                    Directory.Delete(child, recursive: true);
                    write($"Deleted {shown}\n");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failed++;
                    write($"Could not delete {shown}: {ex.Message}\n");
                }
            }
        }

        write(found == 0 ? "No bin or obj folders found.\n" : $"{found} folder(s) {(dryRun ? "found" : "handled")}, {failed} failed.\n");
        return failed == 0 ? 0 : 1;
    }

    private static bool HasProjectFile(string folder) =>
        ProjectPatterns.Any(pattern => Directory.EnumerateFiles(folder, pattern, SearchOption.TopDirectoryOnly).Any());

    private static List<string> SafeFolders(string folder)
    {
        try
        {
            return Directory.EnumerateDirectories(folder).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
